// 버튜버 리그: 레이어(부위)마다 역할과 깊이를 정하고, 표정·고개 값으로 그림을 움직여 그린다.
// 2D 시차(깊이가 클수록 고개 방향으로 더 움직임) + 고개 기울기 회전 + 머리카락 용수철 + 숨쉬기.
import { createCanvas } from '../core/util.js';

export const ROLES = {
  body: { name: '몸', depth: 0, head: false },
  hairBack: { name: '뒷머리', depth: 0.15, head: true, sway: true },
  head: { name: '얼굴(바탕)', depth: 0.3, head: true },
  blush: { name: '볼터치', depth: 0.45, head: true },
  eyesOpen: { name: '눈 (뜸)', depth: 0.5, head: true },
  pupils: { name: '눈동자', depth: 0.52, head: true },
  eyesClosed: { name: '눈 (감음)', depth: 0.5, head: true },
  brows: { name: '눈썹', depth: 0.55, head: true },
  mouthClosed: { name: '입 (닫힘)', depth: 0.5, head: true },
  mouthOpen: { name: '입 (벌림)', depth: 0.5, head: true },
  mouthA: { name: '입 ㅏ', depth: 0.5, head: true },
  mouthI: { name: '입 ㅣ', depth: 0.5, head: true },
  mouthU: { name: '입 ㅜ', depth: 0.5, head: true },
  mouthE: { name: '입 ㅔ', depth: 0.5, head: true },
  mouthO: { name: '입 ㅗ', depth: 0.5, head: true },
  hairFront: { name: '앞머리', depth: 0.65, head: true, sway: true },
  headOther: { name: '머리에 붙은 것 (모자·액세서리)', depth: 0.4, head: true },
  bodyOther: { name: '몸에 붙은 것', depth: 0.05, head: false },
  expr: { name: '표정 (단축키로 켜기)', depth: 0.5, head: true },
  pngIdle: { name: 'PNG: 가만히', depth: 0, head: false },
  pngTalk: { name: 'PNG: 말할 때', depth: 0, head: false },
  pngBlink: { name: 'PNG: 눈 감음', depth: 0, head: false },
  pngTalkBlink: { name: 'PNG: 말하며 눈 감음', depth: 0, head: false },
  none: { name: '(쓰지 않음)', depth: 0, head: false },
};

/** 레이어 이름으로 역할 짐작 (한국어·영어·일본어) */
export function guessRole(name) {
  const n = String(name || '').toLowerCase().replace(/\s+/g, '');
  const has = (re) => re.test(n);
  if (has(/(idle|대기|기본)/) && has(/png/)) return 'pngIdle';
  if (has(/png/) && has(/(talk|말)/)) return has(/(blink|감)/) ? 'pngTalkBlink' : 'pngTalk';
  if (has(/png/) && has(/(blink|감)/)) return 'pngBlink';
  if (has(/(표정|expr|expression|exp\d)/)) return 'expr';
  if (has(/(눈썹|brow|眉)/)) return 'brows';
  if (has(/(눈동자|동공|pupil|iris|瞳)/)) return 'pupils';
  if (has(/((감은|감음|감김|closed|close|閉).*(눈|eye|目)|(눈|eye|目).*(감|closed|close|閉))/)) return 'eyesClosed';
  if (has(/(눈|eye|目)/)) return 'eyesOpen';
  if (has(/(입|mouth|口)/)) {
    if (has(/(벌|열|open|開)/)) return 'mouthOpen';
    if (has(/(닫|다문|close|閉)/)) return 'mouthClosed';
    if (has(/[_-]?(ㅏ|아|a)$/)) return 'mouthA';
    if (has(/[_-]?(ㅣ|이|i)$/)) return 'mouthI';
    if (has(/[_-]?(ㅜ|우|u)$/)) return 'mouthU';
    if (has(/[_-]?(ㅔ|에|e)$/)) return 'mouthE';
    if (has(/[_-]?(ㅗ|오|o)$/)) return 'mouthO';
    return 'mouthClosed';
  }
  if (has(/(앞머리|frontha|bang|前髪)/)) return 'hairFront';
  if (has(/(뒷머리|backha|hairback|後ろ髪|後髪)/)) return 'hairBack';
  if (has(/(머리카락|hair|髪)/)) return 'hairFront';
  if (has(/(볼|홍조|blush|頬|チーク)/)) return 'blush';
  if (has(/(얼굴|face|head|顔|頭)/)) return 'head';
  if (has(/(몸|body|torso|옷|cloth|体|服|목|neck)/)) return 'body';
  if (has(/(모자|hat|리본|ribbon|귀|ear|액세서리|acc)/)) return 'headOther';
  if (has(/(배경|background|bg|paper|용지)/)) return 'none';
  return 'headOther';
}

// ---------------------------------------------------------------------
// 부위 그림 준비
// ---------------------------------------------------------------------
/** 캔버스에서 투명하지 않은 영역 */
export function opaqueRect(c) {
  const { width: w, height: h } = c, d = c.getContext('2d').getImageData(0, 0, w, h).data;
  let x0 = w, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (d[(y * w + x) * 4 + 3] > 8) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
  return x1 < 0 ? null : { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
}

/** 원본 캔버스 → 부위 { img(잘라 낸 캔버스), x, y, w, h } */
export function cutPart(src, scale = 1) {
  const r = opaqueRect(src);
  if (!r) return null;
  const c = createCanvas(Math.max(1, Math.round(r.w * scale)), Math.max(1, Math.round(r.h * scale)));
  const x = c.getContext('2d');
  x.imageSmoothingQuality = 'high';
  x.drawImage(src, r.x, r.y, r.w, r.h, 0, 0, c.width, c.height);
  return { img: c, x: r.x * scale, y: r.y * scale, w: c.width, h: c.height };
}

/**
 * 리그 만들기. parts: [{ id, name, role, depth, key, img, x, y, w, h, order }] (order = 아래→위)
 * 크기 w,h 와 목 위치(고개 회전 중심)를 부위 위치로 정한다.
 */
export function buildRig(parts, w, h, opts = {}) {
  const ps = parts.filter(p => p.role && p.role !== 'none' && p.img).map((p, i) => ({ order: i, depth: ROLES[p.role]?.depth ?? 0.3, ...p }));
  const headParts = ps.filter(p => ROLES[p.role]?.head);
  const face = ps.find(p => p.role === 'head') || headParts[0];
  // 고개를 돌리는 중심: 얼굴 아래 가운데 (목)
  const neck = face ? { x: face.x + face.w / 2, y: face.y + face.h * 0.95 } : { x: w / 2, y: h * 0.6 };
  const faceH = face ? face.h : h * 0.4;
  const png = ps.some(p => p.role.startsWith('png'));
  return { w, h, parts: ps, neck, faceH, png, opts: { motion: 1, mouthGain: 1, smooth: 0.5, mirror: true, ...opts } };
}

// ---------------------------------------------------------------------
// 상태 (표정·고개 값) → 그리기
// ---------------------------------------------------------------------
/** 기본 상태 값: 고개 yaw/pitch/roll -1~1, 눈 감음 0~1, 입 0~1, 웃음 0~1, 눈썹 -1~1, 눈동자 -1~1 */
export function neutral() {
  return { yaw: 0, pitch: 0, roll: 0, blinkL: 0, blinkR: 0, mouth: 0, smile: 0, funnel: 0, brow: 0, lookX: 0, lookY: 0, talk: 0, expr: 0, t: 0 };
}

/** 머리카락 용수철 (고개가 움직이면 반대로 살짝 흔들렸다가 돌아옴) */
export function makePhysics() { return { a: 0, v: 0, lastYaw: 0, lastRoll: 0 }; }
export function stepPhysics(ph, st, dt) {
  dt = Math.min(0.05, Math.max(0.001, dt));
  const push = ((st.yaw - ph.lastYaw) + (st.roll - ph.lastRoll) * 1.5) / dt;
  ph.lastYaw = st.yaw; ph.lastRoll = st.roll;
  const k = 90, c = 9;                               // 단단함·감쇠
  ph.v += (-k * ph.a - c * ph.v - push * 0.6) * dt;
  ph.a += ph.v * dt;
  ph.a = Math.max(-0.35, Math.min(0.35, ph.a));
  return ph.a;
}

/** 입 모양 고르기: 벌림 정도와 모양 값 → 역할 이름 */
export function mouthRole(rig, st) {
  const has = (r) => rig.parts.some(p => p.role === r);
  const open = st.mouth;
  if (open < 0.12) return has('mouthClosed') ? 'mouthClosed' : null;
  const vowels = ['mouthA', 'mouthI', 'mouthU', 'mouthE', 'mouthO'].filter(has);
  if (vowels.length) {
    if (st.funnel > 0.35 && has('mouthO') && open > 0.35) return 'mouthO';
    if (st.funnel > 0.35 && has('mouthU')) return 'mouthU';
    if (st.smile > 0.45 && open < 0.4 && has('mouthI')) return 'mouthI';
    if (st.smile > 0.35 && has('mouthE')) return 'mouthE';
    if (has('mouthA')) return 'mouthA';
    return vowels[0];
  }
  return has('mouthOpen') ? 'mouthOpen' : has('mouthClosed') ? 'mouthClosed' : null;
}

/**
 * 한 장 그리기. ctx 크기는 rig.w × rig.h 기준 (scale 로 맞춤)
 * st: 상태, ph: 머리카락 값(-0.35~0.35), o: { scale, bg }
 */
export function renderRig(ctx, rig, st, sway = 0, o = {}) {
  const s = o.scale || 1, M = rig.opts.motion ?? 1;
  ctx.save();
  ctx.setTransform(s, 0, 0, s, 0, 0);
  if (o.bg) { ctx.fillStyle = o.bg; ctx.fillRect(0, 0, rig.w, rig.h); } else ctx.clearRect(0, 0, rig.w, rig.h);
  ctx.imageSmoothingQuality = 'high';
  const breath = Math.sin((st.t || 0) * Math.PI * 2 / 3.6);
  const P = rig.faceH * 0.12 * M;                    // 고개를 돌렸을 때 가장 앞 부위가 움직이는 거리
  const bounce = rig.png ? -Math.abs(st.talk) * rig.h * 0.02 : 0;
  // PNG 버튜버: 상태에 맞는 그림 한 장 (말하면 살짝 튐)
  if (rig.png) {
    const blink = Math.max(st.blinkL, st.blinkR) > 0.5, talk = st.talk > 0.5;
    const pick = (r) => rig.parts.find(p => p.role === r);
    const p = (talk && blink && pick('pngTalkBlink')) || (talk && pick('pngTalk')) || (blink && pick('pngBlink')) || pick('pngIdle') || rig.parts[0];
    if (p) ctx.drawImage(p.img, p.x, p.y + bounce + breath * rig.h * 0.003, p.w, p.h);
    for (const q of rig.parts) if (q.role === 'expr' && st.expr && q.key === st.expr) ctx.drawImage(q.img, q.x, q.y + bounce, q.w, q.h);
    ctx.restore();
    return;
  }
  const mouth = mouthRole(rig, st);
  const blinkBoth = (st.blinkL + st.blinkR) / 2;
  const rollRad = st.roll * 0.35 * M;                // 최대 약 20°
  for (const p of rig.parts) {
    const R = ROLES[p.role] || {};
    // 보일지
    if (p.role === 'eyesOpen' || p.role === 'pupils') { if (blinkBoth > 0.55 && rig.parts.some(q => q.role === 'eyesClosed')) continue; }
    if (p.role === 'eyesClosed' && blinkBoth <= 0.55) continue;
    if (p.role.startsWith('mouth') && p.role !== mouth) continue;
    if (p.role === 'expr' && !(st.expr && p.key === st.expr)) continue;
    let alpha = 1;
    if (p.role === 'blush') alpha = Math.min(1, 0.25 + st.smile * 0.9 + (st.expr && p.key === st.expr ? 1 : 0));
    ctx.save();
    ctx.globalAlpha = alpha;
    const breathY = breath * rig.h * (R.head ? 0.004 : 0.0025);
    if (R.head) {
      // 목을 중심으로 기울이고, 깊이만큼 고개 방향으로 옮김
      ctx.translate(rig.neck.x, rig.neck.y + breathY);
      ctx.rotate(rollRad);
      ctx.translate(-rig.neck.x, -rig.neck.y);
      ctx.translate(st.yaw * P * p.depth * 2, st.pitch * P * p.depth * 1.4);
      // 고개를 돌리면 얼굴이 살짝 좁아 보이게 (가로 눌림)
      const squash = 1 - Math.abs(st.yaw) * 0.06 * M;
      ctx.translate(rig.neck.x, 0); ctx.scale(squash, 1); ctx.translate(-rig.neck.x, 0);
    } else {
      ctx.translate(0, breathY);
      // 숨쉴 때 몸이 아주 조금 커졌다 작아졌다
      ctx.translate(p.x + p.w / 2, p.y + p.h); ctx.scale(1, 1 + breath * 0.004); ctx.translate(-(p.x + p.w / 2), -(p.y + p.h));
    }
    let dx = 0, dy = 0;
    if (p.role === 'pupils') { dx = st.lookX * p.w * 0.08; dy = st.lookY * p.h * 0.15; }
    if (p.role === 'brows') dy = -st.brow * rig.faceH * 0.03;
    if (R.sway && sway) {
      // 머리카락: 윗부분을 중심으로 흔들림 (뒷머리는 반대로 조금 더)
      const px = p.x + p.w / 2, py = p.y + p.h * 0.1;
      ctx.translate(px, py); ctx.rotate(sway * (p.role === 'hairBack' ? -0.6 : 0.35)); ctx.translate(-px, -py);
    }
    ctx.drawImage(p.img, p.x + dx, p.y + dy, p.w, p.h);
    ctx.restore();
  }
  ctx.restore();
}

// ---------------------------------------------------------------------
// 추적 값 → 상태
// ---------------------------------------------------------------------
/** MediaPipe 얼굴 변환 행렬(열 우선 4x4) → yaw/pitch/roll (라디안) */
export function headAngles(m) {
  const R = (r, c) => m[c * 4 + r];
  const yaw = Math.atan2(R(0, 2), R(2, 2));
  const pitch = Math.asin(Math.max(-1, Math.min(1, -R(1, 2))));
  const roll = Math.atan2(R(1, 0), R(1, 1));
  return { yaw, pitch, roll };
}

/** blendshape 목록 → 상태 값 (mirror: 거울처럼 좌우 바꿈) */
export function faceToState(shapes, matrix, mirror = true) {
  const b = {};
  for (const c of shapes || []) b[c.categoryName] = c.score;
  const g = (k) => b[k] || 0;
  const st = neutral();
  st.blinkL = g(mirror ? 'eyeBlinkRight' : 'eyeBlinkLeft');
  st.blinkR = g(mirror ? 'eyeBlinkLeft' : 'eyeBlinkRight');
  st.mouth = Math.min(1, g('jawOpen') * 1.6);
  st.smile = (g('mouthSmileLeft') + g('mouthSmileRight')) / 2;
  st.funnel = Math.max(g('mouthFunnel'), g('mouthPucker'));
  st.brow = g('browInnerUp') - (g('browDownLeft') + g('browDownRight')) / 2;
  const lx = (g('eyeLookOutLeft') - g('eyeLookInLeft') + g('eyeLookInRight') - g('eyeLookOutRight')) / 2;
  st.lookX = mirror ? -lx : lx;
  st.lookY = ((g('eyeLookDownLeft') + g('eyeLookDownRight')) - (g('eyeLookUpLeft') + g('eyeLookUpRight'))) / 2;
  if (matrix) {
    const a = headAngles(matrix), D = Math.PI / 6;     // 30° = 끝까지
    st.yaw = Math.max(-1, Math.min(1, (mirror ? -a.yaw : a.yaw) / D));
    st.pitch = Math.max(-1, Math.min(1, -a.pitch / D));
    st.roll = Math.max(-1, Math.min(1, (mirror ? -a.roll : a.roll) / D));
  }
  return st;
}

/** 마이크 소리 크기 (RMS) */
export function rms(buf) {
  let s = 0;
  for (let i = 0; i < buf.length; i++) s += buf[i] * buf[i];
  return Math.sqrt(s / Math.max(1, buf.length));
}
/** 소리 크기 → 입 벌림 0~1 (문턱 아래는 0) */
export function levelToMouth(level, gain = 1, gate = 0.02) {
  return Math.max(0, Math.min(1, (level - gate) * 9 * gain));
}

/** 부드럽게: 이전 값 쪽으로 (k = 0 이면 그대로, 1 에 가까울수록 느리게) */
export function smoothState(prev, next, k) {
  if (!prev) return { ...next };
  const out = { ...next };
  for (const key of ['yaw', 'pitch', 'roll', 'mouth', 'smile', 'funnel', 'brow', 'lookX', 'lookY', 'talk']) out[key] = prev[key] + (next[key] - prev[key]) * (1 - k);
  // 눈 깜빡임은 빠르게
  for (const key of ['blinkL', 'blinkR']) out[key] = prev[key] + (next[key] - prev[key]) * (1 - k * 0.3);
  return out;
}

/** 자동 깜빡임 (추적 안 할 때): 3~6초마다 0.12초 */
export function autoBlink(t, seed = 0) {
  const period = 4.2 + Math.sin(seed * 12.9898) * 1.2;
  const ph = (t + seed) % period;
  return ph < 0.12 ? 1 : 0;
}

// ---------------------------------------------------------------------
// .cpvt (아바타 파일) — 부위 그림을 PNG 로 담음
// ---------------------------------------------------------------------
export async function rigToFile(rig, name = '아바타') {
  const parts = [];
  for (const p of rig.parts) parts.push({ name: p.name, role: p.role, depth: p.depth, key: p.key || 0, x: p.x, y: p.y, w: p.w, h: p.h, png: p.img.toDataURL('image/png') });
  return { format: 'comicpad-vtuber', version: 1, name, w: rig.w, h: rig.h, opts: rig.opts, parts };
}
export async function fileToRig(json) {
  if (json?.format !== 'comicpad-vtuber') throw new Error('ComicPad 아바타 파일(.cpvt)이 아닙니다');
  const parts = [];
  for (const p of json.parts) {
    const img = new Image();
    // decode() 는 창이 가려져 있으면 멈출 수 있어 onload 로
    await new Promise((ok, no) => { img.onload = ok; img.onerror = () => no(new Error('아바타 그림을 읽지 못했습니다')); img.src = p.png; });
    const c = createCanvas(p.w, p.h);
    c.getContext('2d').drawImage(img, 0, 0, p.w, p.h);
    parts.push({ ...p, img: c, png: undefined });
  }
  return buildRig(parts, json.w, json.h, json.opts);
}
