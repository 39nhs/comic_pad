// 립싱크 보조: 붙인 소리를 프레임마다 분석해 입 모양(닫힘·반·ㅏ·ㅣ·ㅜ)을 고르고, 입 트랙의 노출표에 자동으로 배치한다.
// 소리 크기 + 대역별 에너지(낮음·가운데·높음)로 모음을 대략 나눈다 (정확한 음성 인식은 아님).
import { app } from '../app.js';
import { audioBuffer } from './animx.js';
import * as A from './anim.js';

export const SHAPES = [
  ['closed', '닫힘 (소리 없음)'],
  ['half', '반쯤 (작은 소리·자음)'],
  ['a', 'ㅏ (크게 벌림)'],
  ['i', 'ㅣ·ㅔ (옆으로)'],
  ['u', 'ㅜ·ㅗ (둥글게)'],
];
// 그림이 없는 모양은 가까운 모양으로 대신
const FALLBACK = { half: ['closed', 'a'], a: ['half', 'i', 'u'], i: ['a', 'half'], u: ['a', 'half'], closed: ['half'] };

/** 실수 FFT (크기만), n 은 2의 거듭제곱 */
function fftMag(re) {
  const n = re.length, im = new Float32Array(n);
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) { const t = re[i]; re[i] = re[j]; re[j] = t; }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = -2 * Math.PI / len, wr = Math.cos(ang), wi = Math.sin(ang);
    for (let i = 0; i < n; i += len) {
      let cr = 1, ci = 0;
      for (let k = 0; k < len / 2; k++) {
        const a = i + k, b = a + len / 2;
        const tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
        const ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
      }
    }
  }
  const out = new Float32Array(n / 2);
  for (let i = 0; i < n / 2; i++) out[i] = Math.hypot(re[i], im[i]);
  return out;
}

/** 프레임마다 { vol, low, mid, high } (vol 은 0~1 로 맞춤) */
export async function analyze(doc = app.doc, nFrames = A.frameCount(doc)) {
  const buf = await audioBuffer(doc);
  if (!buf) throw new Error('붙인 소리가 없습니다 (애니 패널 > 소리 넣기)');
  const sr = buf.sampleRate, ch = buf.getChannelData(0), off = doc.anim.audio.offset || 0;
  const N = 2048, win = new Float32Array(N);
  for (let i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / (N - 1));
  const bin = (hz) => Math.max(1, Math.min(N / 2 - 1, Math.round((hz * N) / sr)));
  const B = { low: [bin(150), bin(700)], mid: [bin(700), bin(1800)], high: [bin(1800), bin(5000)] };
  let t = 0;
  const out = [];
  for (let f = 0; f < nFrames; f++) {
    const dur = A.frameMs(f, doc) / 1000, a = Math.floor((t - off) * sr), b = Math.floor((t + dur - off) * sr);
    t += dur;
    let rms = 0, cnt = 0;
    for (let k = Math.max(0, a); k < Math.min(ch.length, b); k++) { rms += ch[k] * ch[k]; cnt++; }
    rms = cnt ? Math.sqrt(rms / cnt) : 0;
    // 프레임 가운데 둘레로 FFT
    const c = Math.floor((a + b) / 2) - N / 2, seg = new Float32Array(N);
    for (let i = 0; i < N; i++) { const k = c + i; seg[i] = k >= 0 && k < ch.length ? ch[k] * win[i] : 0; }
    const mag = fftMag(seg);
    const band = ([lo, hi]) => { let s = 0; for (let i = lo; i < hi; i++) s += mag[i] * mag[i]; return s; };
    out.push({ vol: rms, low: band(B.low), mid: band(B.mid), high: band(B.high) });
  }
  // 소리 크기는 큰 쪽 95% 값을 1 로
  const sorted = out.map(o => o.vol).sort((x, y) => x - y), ref = sorted[Math.floor(sorted.length * 0.95)] || 1e-6;
  for (const o of out) o.vol = Math.min(1, o.vol / Math.max(1e-6, ref));
  return out;
}

/** 특징 → 입 모양 */
export function classify(feats, { quiet = 0.12, small = 0.32 } = {}) {
  return feats.map(({ vol, low, mid, high }) => {
    if (vol < quiet) return 'closed';
    if (vol < small) return 'half';
    const sum = low + mid + high || 1, l = low / sum, h = high / sum, m = mid / sum;
    if (h + m * 0.5 > 0.55 && h > 0.18) return 'i';         // 높은 소리가 많으면 옆으로 (ㅣ·ㅔ)
    if (l > 0.72 && h < 0.08) return 'u';                   // 낮은 소리만 크면 둥글게 (ㅜ·ㅗ)
    return 'a';
  });
}

/** 너무 짧게 바뀌면 깜빡여 보이므로, minHold 장보다 짧은 모양은 앞 모양에 붙인다 */
export function smooth(shapes, minHold = 2) {
  const s = shapes.slice();
  let i = 0;
  while (i < s.length) {
    let j = i;
    while (j < s.length && s[j] === s[i]) j++;
    if (j - i < minHold && i > 0) for (let k = i; k < j; k++) s[k] = s[i - 1];
    i = j;
  }
  return s;
}

/** 모양 → 셀 id (그림이 없는 모양은 가까운 모양으로) */
export function resolve(map, shape) {
  if (map[shape]) return map[shape];
  for (const alt of FALLBACK[shape] || []) if (map[alt]) return map[alt];
  return Object.values(map).find(Boolean) || null;
}

/** 입 트랙에 자동 배치. map = { closed: 셀id, half, a, i, u }. extend = 소리 길이만큼 프레임 늘리기 */
export async function applyLipSync(track, map, { extend = false, minHold = 2 } = {}) {
  const doc = app.doc;
  let n = A.frameCount(doc);
  if (extend) {
    const buf = await audioBuffer(doc);
    const total = (buf.duration + (doc.anim.audio.offset || 0)) * 1000;
    let t = 0, k = 0;
    while (t < total && k < 10000) { t += A.frameMs(k, doc); k++; }
    n = Math.max(n, k);
  }
  const shapes = smooth(classify(await analyze(doc, n)), minHold);
  A.editAnim('립싱크 자동 배치', () => {
    // 늘어난 프레임에서 다른 트랙은 마지막 그림을 이어서 보여 줌
    if (n > A.frameCount(doc)) {
      for (const t of A.tracks(doc)) { const xs = t.meta.xs, last = xs.at(-1) ?? null; while (xs.length < n) xs.push(t === track ? null : last); }
      doc.anim.length = n;
    }
    track.meta.lipMap = { ...map };
    for (let f = 0; f < n; f++) track.meta.xs[f] = resolve(map, shapes[f]);
  });
  const count = {};
  for (const s of shapes) count[s] = (count[s] || 0) + 1;
  return { frames: n, count };
}
