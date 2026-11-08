// 사진 → 데생 인형 자세 / 손 모양 (MediaPipe, Apache-2.0). 사진은 이 PC 밖으로 나가지 않는다.
import { app } from '../app.js';
import { pickFile } from '../core/util.js';
import * as R from './runtime.js';
import { withKey } from '../core/net.js';
import { retargetPoints } from '../three/bvh.js';

let visionP = null;
const vision = () => (visionP ||= (async () => {
  const v = await import(new URL('../../vendor/ai/mediapipe/vision_bundle.mjs', import.meta.url).href);
  const files = await v.FilesetResolver.forVisionTasks(new URL(new URL('../../vendor/ai/mediapipe/wasm', import.meta.url).href, location.href).href);
  return { v, files };
})());

const tasks = {};
async function landmarker(kind) {
  if (tasks[kind]) return tasks[kind];
  const id = kind === 'pose' ? 'pose-mp' : 'hand-mp';
  if (!(await R.installed(kind)).some(m => m.id === id)) throw new Error('모델이 없습니다. AI > AI 모델 관리에서 "MediaPipe ' + (kind === 'pose' ? '포즈' : '손') + '" 를 받으세요');
  const { v, files } = await vision();
  const file = kind === 'pose' ? 'pose_landmarker_full.task' : 'hand_landmarker.task';
  const Cls = kind === 'pose' ? v.PoseLandmarker : v.HandLandmarker;
  tasks[kind] = await Cls.createFromOptions(files, {
    baseOptions: { modelAssetPath: withKey(R.modelUrl(id, file)), delegate: 'CPU' },
    runningMode: 'IMAGE', ...(kind === 'pose' ? { numPoses: 1 } : { numHands: 1 }),
  });
  return tasks[kind];
}

async function loadImage(f) {
  const url = URL.createObjectURL(f);
  try {
    const img = new Image();
    await new Promise((ok, no) => { img.onload = ok; img.onerror = () => no(new Error('그림 파일을 열지 못했습니다')); img.src = url; });
    return img;
  } finally { setTimeout(() => URL.revokeObjectURL(url), 1000); }
}

/** MediaPipe 몸 랜드마크(월드 좌표, 미터) → 인형 포즈 */
export function poseFromLandmarks(L) {
  // MediaPipe: x 오른쪽(사람의 왼쪽), y 아래, z 카메라 쪽이 작음 → 인형: y 위, 사람은 +z 를 봄
  const P = (i) => (L[i] && (L[i].visibility ?? 1) > 0.3 ? [L[i].x, -L[i].y, -L[i].z] : null);
  const mid = (a, b) => (a && b ? a.map((v, k) => (v + b[k]) / 2) : null);
  const lerp = (a, b, t) => (a && b ? a.map((v, k) => v + (b[k] - v) * t) : null);
  const hips = mid([L[23].x, -L[23].y, -L[23].z], [L[24].x, -L[24].y, -L[24].z]);
  const sh = mid([L[11].x, -L[11].y, -L[11].z], [L[12].x, -L[12].y, -L[12].z]);
  const head = mid(P(7), P(8)) || P(0);
  const Q = {
    hips, spine: lerp(hips, sh, 0.35), chest: lerp(hips, sh, 0.7), neck: sh, head,
    clavL: sh, clavR: sh, upperL: P(11), upperR: P(12), foreL: P(13), foreR: P(14), handL: P(15), handR: P(16),
    thighL: P(23), thighR: P(24), shinL: P(25), shinR: P(26), footL: P(27), footR: P(28),
  };
  return retargetPoints((k) => Q[k] || null);
}

/** MediaPipe 손 랜드마크 21개 → 손 모델 포즈 (손가락 굽힘) */
export function handFromLandmarks(L) {
  const V = (a, b) => [L[b].x - L[a].x, L[b].y - L[a].y, L[b].z - L[a].z];
  const ang = (u, v) => { const lu = Math.hypot(...u) || 1, lv = Math.hypot(...v) || 1; return Math.acos(Math.max(-1, Math.min(1, (u[0] * v[0] + u[1] * v[1] + u[2] * v[2]) / (lu * lv)))) * 180 / Math.PI; };
  const lim = (d) => Math.round(Math.min(110, Math.max(0, d)));
  const pose = {};
  const F = { index: [5, 6, 7, 8], middle: [9, 10, 11, 12], ring: [13, 14, 15, 16], pinky: [17, 18, 19, 20] };
  for (const [n, [a, b, c, d]] of Object.entries(F)) {
    pose[n + '1'] = [-lim(ang(V(0, a), V(a, b))), 0, 0];
    pose[n + '2'] = [-lim(ang(V(a, b), V(b, c))), 0, 0];
    pose[n + '3'] = [-lim(ang(V(b, c), V(c, d))), 0, 0];
  }
  const t1 = lim(ang(V(0, 1), V(1, 2))), t2 = lim(ang(V(1, 2), V(2, 3))), t3 = lim(ang(V(2, 3), V(3, 4)));
  pose.thumb1 = [-Math.round(t1 * 0.6), 0, -Math.round(t1 * 0.8)];
  pose.thumb2 = [-t2, 0, -Math.round(t2 * 0.5)];
  pose.thumb3 = [-t3, 0, 0];
  return pose;
}

/** 사진 고르기 → 포즈. kind: 'pose' | 'hand' */
export async function poseFromPhoto(kind = 'pose') {
  const f = await pickFile('image/*');
  if (!f) return null;
  return app.task(kind === 'pose' ? '사진에서 자세 읽는 중' : '사진에서 손 모양 읽는 중', async () => {
    const lm = await landmarker(kind);
    const img = await loadImage(f);
    const res = lm.detect(img);
    if (kind === 'pose') {
      const L = res.worldLandmarks?.[0];
      if (!L) throw new Error('사진에서 사람을 찾지 못했습니다 (몸 전체가 보이는 사진이 좋습니다)');
      return poseFromLandmarks(L);
    }
    const L = res.worldLandmarks?.[0];
    if (!L) throw new Error('사진에서 손을 찾지 못했습니다');
    return handFromLandmarks(L);
  }, { delay: 0 });
}
