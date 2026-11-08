// BVH 모션 캡처 파일 → 데생 인형 포즈
// 관절 이름이 달라도(CMU·Mixamo·Biovision 등) 맞춰 보고, 뼈 방향을 따라 인형 관절 각도를 구한다(비틀림은 일부만).
import { M, mannequinMatrices } from './engine.js';

export function parseBVH(text) {
  const tok = text.split(/\s+/).filter(Boolean);
  let i = 0;
  const joints = [];
  const stack = [];
  const next = () => tok[i++];
  if (next()?.toUpperCase() !== 'HIERARCHY') throw new Error('BVH 파일이 아닙니다');
  let channelCount = 0;
  while (i < tok.length) {
    const t = next();
    const u = t.toUpperCase();
    if (u === 'ROOT' || u === 'JOINT') {
      const j = { name: next(), parent: stack.length ? stack[stack.length - 1] : -1, offset: [0, 0, 0], channels: [], ch0: 0, end: false };
      joints.push(j);
      stack.push(joints.length - 1);
    } else if (u === 'END') {
      next();   // Site
      const j = { name: joints[stack[stack.length - 1]].name + '_end', parent: stack[stack.length - 1], offset: [0, 0, 0], channels: [], ch0: 0, end: true };
      joints.push(j);
      stack.push(joints.length - 1);
    } else if (u === 'OFFSET') {
      joints[stack[stack.length - 1]].offset = [+next(), +next(), +next()];
    } else if (u === 'CHANNELS') {
      const j = joints[stack[stack.length - 1]], n = +next();
      j.ch0 = channelCount;
      for (let k = 0; k < n; k++) j.channels.push(next().toLowerCase());
      channelCount += n;
    } else if (t === '}') {
      stack.pop();
    } else if (u === 'MOTION') break;
  }
  next();                     // Frames:
  const frames = +next();
  next(); next();             // Frame Time:
  const frameTime = +next();
  const data = [];
  for (let f = 0; f < frames && i < tok.length; f++) {
    const row = new Float32Array(channelCount);
    for (let k = 0; k < channelCount; k++) row[k] = +tok[i++];
    data.push(row);
  }
  return { joints, frames: data, frameTime: frameTime || 1 / 30 };
}

const D = Math.PI / 180;
/** 한 프레임의 관절별 월드 행렬 */
function fk(bvh, f) {
  const row = bvh.frames[f], out = [];
  bvh.joints.forEach((j, k) => {
    let t = [...j.offset], R = M.ident();
    j.channels.forEach((c, n) => {
      const v = row[j.ch0 + n];
      if (c === 'xposition') t[0] += v; else if (c === 'yposition') t[1] += v; else if (c === 'zposition') t[2] += v;
      else if (c === 'xrotation') R = M.mul(R, M.rotX(v * D));
      else if (c === 'yrotation') R = M.mul(R, M.rotY(v * D));
      else if (c === 'zrotation') R = M.mul(R, M.rotZ(v * D));
    });
    const local = M.mul(M.translate(...t), R);
    out[k] = j.parent < 0 ? local : M.mul(out[j.parent], local);
  });
  return out;
}

// ---- 이름 맞추기 ----
const norm = (s) => s.toLowerCase().replace(/^.*:/, '').replace(/[\s_.-]/g, '');
const SIDE = (side, names) => names.map(n => n.replace(/^left/, side === 'L' ? 'left' : 'right').replace(/^l(?=[a-z])/, side === 'L' ? 'l' : 'r'));
const ALIASES = {
  hips: ['hips', 'pelvis', 'hip', 'root'],
  spine: ['spine', 'lowerback', 'abdomen', 'spine01'],
  chest: ['spine2', 'chest', 'upperchest', 'spine1', 'spine02', 'chest2'],
  neck: ['neck', 'neck1', 'neck01'],
  head: ['head'],
  clav: ['leftshoulder', 'lcollar', 'leftcollar', 'lclavicle', 'leftclavicle'],
  upper: ['leftarm', 'lshldr', 'leftuparm', 'lupperarm', 'leftupperarm', 'lshoulder'],
  fore: ['leftforearm', 'lforearm', 'leftlowarm', 'llowerarm', 'leftelbow'],
  hand: ['lefthand', 'lhand', 'leftwrist'],
  thigh: ['leftupleg', 'lthigh', 'lefthip', 'lhip', 'leftthigh', 'lupperleg'],
  shin: ['leftleg', 'lshin', 'leftknee', 'lknee', 'leftlowleg', 'llowerleg'],
  foot: ['leftfoot', 'lfoot', 'leftankle'],
  toe: ['lefttoebase', 'ltoe', 'lefttoe', 'lefttoes', 'lefttoebaseend'],
};

function findJoints(bvh) {
  const by = new Map(bvh.joints.map((j, k) => [norm(j.name), k]));
  const pick = (names) => { for (const n of names) if (by.has(n)) return by.get(n); return -1; };
  const m = {};
  for (const k of ['hips', 'spine', 'chest', 'neck', 'head']) m[k] = pick(ALIASES[k]);
  if (m.chest === m.spine) m.chest = -1;
  for (const s of ['L', 'R']) for (const k of ['clav', 'upper', 'fore', 'hand', 'thigh', 'shin', 'foot', 'toe']) m[k + s] = pick(SIDE(s, ALIASES[k]));
  return m;
}

// ---- 3x3 회전 도우미 (4x4 열 우선 행렬의 회전 부분) ----
const rot3 = (m) => [m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10]];          // 열 우선
const apply3 = (r, v) => [r[0] * v[0] + r[3] * v[1] + r[6] * v[2], r[1] * v[0] + r[4] * v[1] + r[7] * v[2], r[2] * v[0] + r[5] * v[1] + r[8] * v[2]];
const tr3 = (r) => [r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]];
const mul3 = (a, b) => { const o = []; for (let c = 0; c < 3; c++) for (let rr = 0; rr < 3; rr++) o[c * 3 + rr] = a[rr] * b[c * 3] + a[3 + rr] * b[c * 3 + 1] + a[6 + rr] * b[c * 3 + 2]; return o; };
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const nrm = (v) => { const l = Math.hypot(...v) || 1; return [v[0] / l, v[1] / l, v[2] / l]; };
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const pos = (m) => [m[12], m[13], m[14]];

/** a 방향을 b 방향으로 돌리는 가장 짧은 회전 (3x3 열 우선) */
function arc(a, b) {
  a = nrm(a); b = nrm(b);
  const v = cross(a, b), c = dot(a, b);
  if (c < -0.9999) { const ax = nrm(Math.abs(a[0]) < 0.9 ? cross(a, [1, 0, 0]) : cross(a, [0, 1, 0])); return axisAngle(ax, Math.PI); }
  const k = 1 / (1 + c);
  return [
    v[0] * v[0] * k + c, v[1] * v[0] * k + v[2], v[2] * v[0] * k - v[1],
    v[0] * v[1] * k - v[2], v[1] * v[1] * k + c, v[2] * v[1] * k + v[0],
    v[0] * v[2] * k + v[1], v[1] * v[2] * k - v[0], v[2] * v[2] * k + c,
  ];
}
function axisAngle([x, y, z], t) {
  const c = Math.cos(t), s = Math.sin(t), C = 1 - c;
  return [x * x * C + c, y * x * C + z * s, z * x * C - y * s, x * y * C - z * s, y * y * C + c, z * y * C + x * s, x * z * C + y * s, y * z * C - x * s, z * z * C + c];
}
/** 두 방향(위·옆)으로 정한 좌표계 */
function frame3(up, side) {
  const y = nrm(up), z = nrm(cross(side, y)), x = cross(y, z);
  return [...x, ...y, ...z];
}
/** R = Rz·Ry·Rx 의 각도(도) — engine 의 M.euler 와 같은 순서 */
function euler(r) {
  const r00 = r[0], r10 = r[1], r20 = r[2], r21 = r[5], r22 = r[8];
  const b = Math.asin(Math.max(-1, Math.min(1, -r20)));
  let a, c;
  if (Math.abs(r20) < 0.9999) { a = Math.atan2(r21, r22); c = Math.atan2(r10, r00); } else { a = Math.atan2(-r[7], r[4]); c = 0; }
  return [a / D, b / D, c / D].map(v => Math.round(v * 10) / 10);
}

/** BVH 한 프레임 → 인형 포즈 { 관절: [x, y, z] } */
export function retarget(bvh, f, map = findJoints(bvh)) {
  const W = fk(bvh, f);
  const Q = (key) => (map[key] >= 0 ? pos(W[map[key]]) : null);
  return retargetPoints(Q, map.hips >= 0 ? rot3(W[map.hips]) : null);
}

/**
 * 관절 위치들 → 인형 포즈. Q(이름) = [x, y, z] 또는 null (y 위, 사람은 +z 를 봄, 왼쪽 = +x).
 * 이름: hips spine chest neck head / clavL upperL foreL handL thighL shinL footL (R 도 같음)
 * hipsR 가 없으면 골반 회전을 엉덩이·어깨 위치로 계산 (사진 포즈 추정용)
 */
export function retargetPoints(Q, hipsR = null) {
  const pose = {};
  const rest = mannequinMatrices({}, null);   // 인형의 차렷 자세
  const restDir = (j, child) => nrm(sub(pos(rest[child]), pos(rest[j])));
  const world = {};
  const setRot = (j, parentRot, want) => { const E = mul3(tr3(parentRot), want); pose[j] = euler(E); world[j] = want; };
  const boneTo = (j, parent, child, a, b) => {
    if (!a || !b) { world[j] = world[parent]; return; }
    const d0 = apply3(world[parent], restDir(j, child));                      // 지금 부모 방향 그대로일 때 뼈 방향
    const want = mul3(arc(d0, sub(b, a)), world[parent]);
    setRot(j, world[parent], want);
  };
  if (!hipsR) {
    const hp = Q('hips'), up = Q('chest') || Q('neck'), tl = Q('thighL'), tr = Q('thighR');
    if (hp && up && tl && tr) {
      const restF = frame3(sub(pos(rest.chest), pos(rest.hips)), sub(pos(rest.thighL), pos(rest.thighR)));
      hipsR = mul3(frame3(sub(up, hp), sub(tl, tr)), tr3(restF));
    } else hipsR = [1, 0, 0, 0, 1, 0, 0, 0, 1];
  }
  pose.hips = euler(hipsR); world.hips = hipsR;
  boneTo('spine', 'hips', 'chest', Q('spine'), Q('chest') || Q('neck'));
  // 가슴: 위(목 쪽)와 옆(어깨 선) 두 방향으로 비틀림까지
  const chA = Q('chest') || Q('spine'), nk = Q('neck'), lA = Q('upperL'), rA = Q('upperR');
  if (chA && nk && lA && rA) {
    const restF = frame3(sub(pos(rest.neck), pos(rest.chest)), sub(pos(rest.upperArmL), pos(rest.upperArmR)));
    const wantF = frame3(sub(nk, chA), sub(lA, rA));
    setRot('chest', world.spine, mul3(wantF, tr3(restF)));
  } else world.chest = world.spine;
  boneTo('neck', 'chest', 'head', Q('neck'), Q('head'));
  world.head = world.neck;
  for (const s of ['L', 'R']) {
    boneTo('shoulder' + s, 'chest', 'upperArm' + s, Q('clav' + s) || Q('chest'), Q('upper' + s));
    boneTo('upperArm' + s, 'shoulder' + s, 'foreArm' + s, Q('upper' + s), Q('fore' + s));
    boneTo('foreArm' + s, 'upperArm' + s, 'hand' + s, Q('fore' + s), Q('hand' + s));
    boneTo('thigh' + s, 'hips', 'shin' + s, Q('thigh' + s), Q('shin' + s));
    boneTo('shin' + s, 'thigh' + s, 'foot' + s, Q('shin' + s), Q('foot' + s));
  }
  for (const k of Object.keys(pose)) if (pose[k].every(v => Math.abs(v) < 0.05)) delete pose[k];
  return pose;
}

/** 파일 전체 → 포즈 목록 (너무 길면 골고루 추림) */
export function bvhPoses(text, max = 300) {
  const bvh = parseBVH(text);
  const map = findJoints(bvh);
  const found = Object.values(map).filter(v => v >= 0).length;
  if (map.hips < 0 || found < 8) throw new Error('관절 이름을 알아보지 못했습니다 (사람 모양 BVH 가 아닌 것 같습니다)');
  const n = bvh.frames.length, step = Math.max(1, Math.ceil(n / max)), poses = [];
  for (let f = 0; f < n; f += step) poses.push(retarget(bvh, f, map));
  return { poses, fps: 1 / (bvh.frameTime * step), frames: n, matched: found };
}
