// 작은 3D 엔진 (외부 라이브러리 없음): 행렬 수학, 기본 도형, 데생 인형, GLB/GLTF/OBJ 불러오기

// ---------------------------------------------------------------- 행렬 (열 우선 4x4)
export const M = {
  ident() { const m = new Float32Array(16); m[0] = m[5] = m[10] = m[15] = 1; return m; },
  mul(a, b) {
    const o = new Float32Array(16);
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
      o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    }
    return o;
  },
  translate(x, y, z) { const m = M.ident(); m[12] = x; m[13] = y; m[14] = z; return m; },
  scale(x, y = x, z = x) { const m = M.ident(); m[0] = x; m[5] = y; m[10] = z; return m; },
  rotX(a) { const m = M.ident(), c = Math.cos(a), s = Math.sin(a); m[5] = c; m[6] = s; m[9] = -s; m[10] = c; return m; },
  rotY(a) { const m = M.ident(), c = Math.cos(a), s = Math.sin(a); m[0] = c; m[2] = -s; m[8] = s; m[10] = c; return m; },
  rotZ(a) { const m = M.ident(), c = Math.cos(a), s = Math.sin(a); m[0] = c; m[1] = s; m[4] = -s; m[5] = c; return m; },
  /** 회전 각(도) [x,y,z] → Rz·Ry·Rx (x 먼저) */
  euler(r) { const d = Math.PI / 180; return M.mul(M.rotZ(r[2] * d), M.mul(M.rotY(r[1] * d), M.rotX(r[0] * d))); },
  trs(t, r, s) { return M.mul(M.translate(t[0], t[1], t[2]), M.mul(M.euler(r), typeof s === 'number' ? M.scale(s) : M.scale(s[0], s[1], s[2]))); },
  quat(q) {
    const [x, y, z, w] = q, m = M.ident();
    m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y + z * w); m[2] = 2 * (x * z - y * w);
    m[4] = 2 * (x * y - z * w); m[5] = 1 - 2 * (x * x + z * z); m[6] = 2 * (y * z + x * w);
    m[8] = 2 * (x * z + y * w); m[9] = 2 * (y * z - x * w); m[10] = 1 - 2 * (x * x + y * y);
    return m;
  },
  persp(fovy, aspect, near, far) {
    const f = 1 / Math.tan(fovy / 2), m = new Float32Array(16);
    m[0] = f / aspect; m[5] = f; m[10] = (far + near) / (near - far); m[11] = -1; m[14] = 2 * far * near / (near - far);
    return m;
  },
  lookAt(e, t, up) {
    let zx = e[0] - t[0], zy = e[1] - t[1], zz = e[2] - t[2];
    let l = Math.hypot(zx, zy, zz) || 1; zx /= l; zy /= l; zz /= l;
    let xx = up[1] * zz - up[2] * zy, xy = up[2] * zx - up[0] * zz, xz = up[0] * zy - up[1] * zx;
    l = Math.hypot(xx, xy, xz) || 1; xx /= l; xy /= l; xz /= l;
    const yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
    const m = M.ident();
    m[0] = xx; m[4] = xy; m[8] = xz; m[1] = yx; m[5] = yy; m[9] = yz; m[2] = zx; m[6] = zy; m[10] = zz;
    m[12] = -(xx * e[0] + xy * e[1] + xz * e[2]); m[13] = -(yx * e[0] + yy * e[1] + yz * e[2]); m[14] = -(zx * e[0] + zy * e[1] + zz * e[2]);
    return m;
  },
  /** 법선 행렬 (왼쪽 위 3x3 의 역전치) */
  normal3(m) {
    const a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
    const A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
    const det = a * A + b * B + c * C || 1e-9;
    return new Float32Array([A / det, B / det, C / det, -(b * i - c * h) / det, (a * i - c * g) / det, -(a * h - b * g) / det, (b * f - c * e) / det, -(a * f - c * d) / det, (a * e - b * d) / det]);
  },
  point(m, p) { return [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12], m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13], m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]]; },
};

// ---------------------------------------------------------------- 메시
/** pos/nrm: Float32Array(xyz), uv: Float32Array(uv) | null, idx: Uint32Array */
function mesh(pos, nrm, idx, uv = null) { return { pos: new Float32Array(pos), nrm: new Float32Array(nrm), idx: new Uint32Array(idx), uv: uv ? new Float32Array(uv) : null }; }

export function computeNormals(pos, idx) {
  const n = new Float32Array(pos.length);
  for (let i = 0; i < idx.length; i += 3) {
    const a = idx[i] * 3, b = idx[i + 1] * 3, c = idx[i + 2] * 3;
    const ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
    const vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
    const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    for (const k of [a, b, c]) { n[k] += nx; n[k + 1] += ny; n[k + 2] += nz; }
  }
  for (let i = 0; i < n.length; i += 3) { const l = Math.hypot(n[i], n[i + 1], n[i + 2]) || 1; n[i] /= l; n[i + 1] /= l; n[i + 2] /= l; }
  return n;
}

export function boxMesh(w = 1, h = 1, d = 1) {
  const x = w / 2, y = h / 2, z = d / 2, pos = [], nrm = [], idx = [];
  const face = (n, u, v) => {
    const b = pos.length / 3;
    for (const [su, sv] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
      pos.push(n[0] * x + u[0] * su * x + v[0] * sv * x, n[1] * y + u[1] * su * y + v[1] * sv * y, n[2] * z + u[2] * su * z + v[2] * sv * z);
      nrm.push(...n);
    }
    idx.push(b, b + 1, b + 2, b, b + 2, b + 3);
  };
  face([1, 0, 0], [0, 0, -1], [0, 1, 0]); face([-1, 0, 0], [0, 0, 1], [0, 1, 0]);
  face([0, 1, 0], [1, 0, 0], [0, 0, -1]); face([0, -1, 0], [1, 0, 0], [0, 0, 1]);
  face([0, 0, 1], [1, 0, 0], [0, 1, 0]); face([0, 0, -1], [-1, 0, 0], [0, 1, 0]);
  return mesh(pos, nrm, idx);
}

/** 캡슐: y=0 에서 y=-len 까지, 반지름 r (len=0 이면 구) */
export function capsuleMesh(r = 0.5, len = 0, seg = 20, rings = 12, r2 = r) {
  const pos = [], nrm = [], idx = [];
  const rows = [];
  for (let i = 0; i <= rings * 2 + 1; i++) {
    const top = i <= rings;
    const th = (top ? i / rings : (i - 1) / rings) * Math.PI / 2 * 1;
    const theta = top ? th : Math.PI / 2 + ((i - rings - 1) / rings) * Math.PI / 2;
    const off = top ? 0 : -len;
    const row = [];
    for (let j = 0; j <= seg; j++) {
      const ph = (j / seg) * Math.PI * 2;
      const nx = Math.sin(theta) * Math.cos(ph), ny = Math.cos(theta), nz = Math.sin(theta) * Math.sin(ph);
      const rr = top ? r : r2;   // 위 반구 r, 아래 반구 r2 (사이 옆면은 원뿔처럼)
      row.push(pos.length / 3);
      pos.push(nx * rr, ny * rr + off, nz * rr); nrm.push(nx, ny, nz);
    }
    rows.push(row);
  }
  for (let i = 0; i < rows.length - 1; i++) for (let j = 0; j < seg; j++) {
    const a = rows[i][j], b = rows[i][j + 1], c = rows[i + 1][j], d = rows[i + 1][j + 1];
    idx.push(a, c, b, b, c, d);
  }
  return mesh(pos, nrm, idx);
}

export const sphereMesh = (r = 0.5) => capsuleMesh(r, 0, 28, 14);

export function cylinderMesh(r = 0.5, h = 1, seg = 28, rTop = r) {
  const pos = [], nrm = [], idx = [];
  const slope = (r - rTop) / h;
  for (let j = 0; j <= seg; j++) {
    const ph = (j / seg) * Math.PI * 2, c = Math.cos(ph), s = Math.sin(ph);
    const l = Math.hypot(1, slope);
    pos.push(c * r, 0, s * r, c * rTop, h, s * rTop);
    nrm.push(c / l, slope / l, s / l, c / l, slope / l, s / l);
  }
  for (let j = 0; j < seg; j++) { const a = j * 2; idx.push(a, a + 1, a + 2, a + 2, a + 1, a + 3); }
  const cap = (y, rad, ny) => {
    if (rad <= 0) return;
    const c0 = pos.length / 3;
    pos.push(0, y, 0); nrm.push(0, ny, 0);
    for (let j = 0; j <= seg; j++) { const ph = (j / seg) * Math.PI * 2; pos.push(Math.cos(ph) * rad, y, Math.sin(ph) * rad); nrm.push(0, ny, 0); }
    for (let j = 0; j < seg; j++) idx.push(c0, c0 + 1 + (ny > 0 ? j + 1 : j), c0 + 1 + (ny > 0 ? j : j + 1));
  };
  cap(0, r, -1); cap(h, rTop, 1);
  // 가운데 정렬
  for (let i = 1; i < pos.length; i += 3) pos[i] -= h / 2;
  return mesh(pos, nrm, idx);
}

export function planeMesh(w = 4, d = 4) {
  return mesh([-w / 2, 0, -d / 2, w / 2, 0, -d / 2, w / 2, 0, d / 2, -w / 2, 0, d / 2], [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0], [0, 2, 1, 0, 3, 2]);
}

/** 메시에 행렬을 구워 넣은 사본 */
function bake(m, mat) {
  const pos = new Float32Array(m.pos.length), nrm = new Float32Array(m.nrm.length), N = M.normal3(mat);
  for (let i = 0; i < m.pos.length; i += 3) {
    const p = M.point(mat, [m.pos[i], m.pos[i + 1], m.pos[i + 2]]);
    pos.set(p, i);
    const x = m.nrm[i], y = m.nrm[i + 1], z = m.nrm[i + 2];
    let nx = N[0] * x + N[3] * y + N[6] * z, ny = N[1] * x + N[4] * y + N[7] * z, nz = N[2] * x + N[5] * y + N[8] * z;
    const l = Math.hypot(nx, ny, nz) || 1;
    nrm[i] = nx / l; nrm[i + 1] = ny / l; nrm[i + 2] = nz / l;
  }
  return { pos, nrm, idx: m.idx, uv: m.uv };
}

export const PRIMITIVES = {
  box: { name: '상자', make: () => boxMesh(1, 1, 1), y: 0.5 },
  sphere: { name: '구', make: () => sphereMesh(0.5), y: 0.5 },
  cylinder: { name: '원기둥', make: () => cylinderMesh(0.4, 1), y: 0.5 },
  cone: { name: '원뿔', make: () => cylinderMesh(0.5, 1, 28, 0.001), y: 0.5 },
  plane: { name: '바닥판', make: () => planeMesh(6, 6), y: 0 },
  wall: { name: '벽', make: () => boxMesh(6, 3, 0.1), y: 1.5 },
  stairs: { name: '계단', make: () => stairsMesh(), y: 0 },
  table: { name: '책상', make: () => tableMesh(), y: 0 },
  chair: { name: '의자', make: () => chairMesh(), y: 0 },
};

function merge(parts) {
  let n = 0;
  const pos = [], nrm = [], idx = [];
  for (const p of parts) {
    for (let i = 0; i < p.pos.length; i++) { pos.push(p.pos[i]); nrm.push(p.nrm[i]); }
    for (const i of p.idx) idx.push(i + n);
    n += p.pos.length / 3;
  }
  return mesh(pos, nrm, idx);
}
const at = (m, x, y, z) => bake(m, M.translate(x, y, z));

function stairsMesh(steps = 8) {
  const parts = [];
  for (let i = 0; i < steps; i++) parts.push(at(boxMesh(1.4, 0.18 * (i + 1), 0.3), 0, 0.09 * (i + 1), -i * 0.3));
  return merge(parts);
}
function tableMesh() {
  const parts = [at(boxMesh(1.2, 0.05, 0.7), 0, 0.72, 0)];
  for (const [x, z] of [[-0.55, -0.3], [0.55, -0.3], [-0.55, 0.3], [0.55, 0.3]]) parts.push(at(boxMesh(0.05, 0.7, 0.05), x, 0.35, z));
  return merge(parts);
}
function chairMesh() {
  const parts = [at(boxMesh(0.45, 0.05, 0.45), 0, 0.45, 0), at(boxMesh(0.45, 0.5, 0.04), 0, 0.72, -0.2)];
  for (const [x, z] of [[-0.2, -0.2], [0.2, -0.2], [-0.2, 0.2], [0.2, 0.2]]) parts.push(at(boxMesh(0.04, 0.45, 0.04), x, 0.225, z));
  return merge(parts);
}

// ---------------------------------------------------------------- 데생 인형
// 관절: [이름, 부모, 오프셋, 모양]. 모양: cap(a→b 캡슐, r, 가로/세로 늘림) / ell(타원구 중심, 반지름)
const L = 1, R = -1;
const BODY = [
  ['hips', null, [0, 0.98, 0], { cap: [[0, 0.03, 0], [0, -0.05, 0]], r: 0.125, r2: 0.14, sx: 1.25, sz: 0.85 }],
  ['spine', 'hips', [0, 0.06, 0], { cap: [[0, 0.01, 0], [0, 0.17, 0]], r: 0.118, r2: 0.11, sx: 1.15, sz: 0.8 }],
  ['chest', 'spine', [0, 0.2, 0], { cap: [[0, 0.02, 0], [0, 0.15, 0]], r: 0.125, r2: 0.15, sx: 1.3, sz: 0.82 }],
  ['neck', 'chest', [0, 0.21, 0], { cap: [[0, 0, 0], [0, 0.08, 0]], r: 0.045 }],
  ['head', 'neck', [0, 0.09, 0], { ell: [0, 0.11, 0.012], rad: [0.092, 0.118, 0.105] }],
  ...[['L', L], ['R', R]].flatMap(([s, k]) => [
    ['shoulder' + s, 'chest', [0.165 * k, 0.15, 0], { ell: [0, 0, 0], rad: [0.06, 0.055, 0.06] }],
    ['upperArm' + s, 'shoulder' + s, [0.025 * k, -0.01, 0], { cap: [[0, 0, 0], [0, -0.27, 0]], r: 0.052, r2: 0.039 }],
    ['foreArm' + s, 'upperArm' + s, [0, -0.29, 0], { cap: [[0, 0, 0], [0, -0.24, 0]], r: 0.042, r2: 0.028 }],
    ['hand' + s, 'foreArm' + s, [0, -0.26, 0], { ell: [0, -0.07, 0], rad: [0.042, 0.08, 0.022] }],
    ['thigh' + s, 'hips', [0.09 * k, -0.06, 0], { cap: [[0, 0, 0], [0, -0.41, 0]], r: 0.085, r2: 0.052 }],
    ['shin' + s, 'thigh' + s, [0, -0.43, 0], { cap: [[0, 0, 0], [0, -0.39, 0]], r: 0.058, r2: 0.034 }],
    ['foot' + s, 'shin' + s, [0, -0.42, 0], { ell: [0, -0.03, 0.055], rad: [0.045, 0.035, 0.11] }],
  ]),
];
export const JOINT_NAMES = {
  hips: '골반', spine: '허리', chest: '가슴', neck: '목', head: '머리',
  shoulderL: '왼 어깨', upperArmL: '왼 위팔', foreArmL: '왼 아래팔', handL: '왼손', thighL: '왼 허벅지', shinL: '왼 정강이', footL: '왼발',
  shoulderR: '오른 어깨', upperArmR: '오른 위팔', foreArmR: '오른 아래팔', handR: '오른손', thighR: '오른 허벅지', shinR: '오른 정강이', footR: '오른발',
};

let bodyMeshes = null;
function partMesh(shape) {
  if (shape.cap) {
    const [a, b] = shape.cap, len = Math.hypot(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
    let m = capsuleMesh(shape.r, len, 22, 10, shape.r2 ?? shape.r);
    // 캡슐은 -Y 방향 → a→b 방향으로 회전
    const dir = [(b[0] - a[0]) / len, (b[1] - a[1]) / len, (b[2] - a[2]) / len];
    let rot = M.ident();
    if (dir[1] > 0.999) rot = M.rotX(Math.PI);
    else if (dir[1] > -0.999) { const ang = Math.acos(-dir[1]); rot = M.mul(M.rotY(Math.atan2(dir[0], dir[2])), M.rotX(-ang)); }
    m = bake(m, M.mul(M.translate(...a), M.mul(rot, M.scale(shape.sx || 1, 1, shape.sz || 1))));
    return m;
  }
  return bake(sphereMesh(1), M.mul(M.translate(...shape.ell), M.scale(...shape.rad)));
}

export function mannequinParts() {
  if (!bodyMeshes) bodyMeshes = Object.fromEntries(BODY.map(([n, , , s]) => [n, partMesh(s)]));
  return bodyMeshes;
}

/** 체형: 1 = 기본 (약 7.5등신) */
export const BODY_DEFAULT = { head: 1, torso: 1, shoulders: 1, hips: 1, arms: 1, legs: 1, girth: 1 };
export const BODY_NAMES = { head: '머리 크기', torso: '몸통 길이', shoulders: '어깨 폭', hips: '골반 폭', arms: '팔 길이', legs: '다리 길이', girth: '두께 (살집)' };
export const BODY_PRESETS = {
  '기본': {},
  '8등신 모델': { head: 0.92, legs: 1.12, arms: 1.05, girth: 0.9 },
  '만화 6등신': { head: 1.3, torso: 0.92, legs: 0.85, arms: 0.9 },
  '어린이': { head: 1.45, torso: 0.8, shoulders: 0.8, hips: 0.85, arms: 0.75, legs: 0.7, girth: 0.9 },
  'SD 3등신': { head: 2.1, torso: 0.7, shoulders: 0.85, arms: 0.55, legs: 0.5, girth: 1.1 },
  '근육질': { shoulders: 1.25, torso: 1.03, girth: 1.3, arms: 1.02 },
  '여성형': { shoulders: 0.88, hips: 1.15, girth: 0.88, head: 1.03 },
  '통통': { girth: 1.45, hips: 1.15, shoulders: 1.05 },
};

/** 관절 회전(도) + 체형 → 부위별 월드 행렬 */
export function mannequinMatrices(pose, body) {
  const b = { ...BODY_DEFAULT, ...(body || {}) }, g = b.girth;
  const limb = (n, k) => (n.startsWith(k) ? 1 : 0);
  const out = {}, world = {};
  for (const [name, parent, off0] of BODY) {
    const off = [...off0];
    let ps = [g, 1, g];
    if (name === 'hips') { off[1] = 0.13 + 0.85 * b.legs; ps = [g * b.hips, 1, g]; }
    else if (name === 'spine' || name === 'neck') { off[1] *= b.torso; ps = [g, b.torso, g]; }
    else if (name === 'chest') { off[1] *= b.torso; ps = [g * b.shoulders, b.torso, g]; }
    else if (name === 'head') ps = [b.head, b.head, b.head];
    else if (limb(name, 'shoulder')) { off[0] *= b.shoulders; off[1] *= b.torso; ps = [g, g, g]; }
    else if (limb(name, 'upperArm')) ps = [g, b.arms, g];
    else if (limb(name, 'foreArm') || limb(name, 'hand')) { off[1] *= b.arms; ps = limb(name, 'hand') ? [1, b.arms ** 0.5, 1] : [g, b.arms, g]; }
    else if (limb(name, 'thigh')) { off[0] *= b.hips; ps = [g, b.legs, g]; }
    else if (limb(name, 'shin')) { off[1] *= b.legs; ps = [g, b.legs, g]; }
    else if (limb(name, 'foot')) { off[1] *= b.legs; ps = [1, 1, 1]; }
    const local = M.mul(M.translate(...off), M.euler(pose[name] || [0, 0, 0]));
    world[name] = parent ? M.mul(world[parent], local) : local;
    out[name] = M.mul(world[name], M.scale(...ps));
  }
  return out;
}

// ---------------------------------------------------------------- 손 (손가락 마디까지)
// 손바닥이 +Z(정면 카메라 쪽), 손가락은 아래(-Y). side: 1 = 오른손, -1 = 왼손
function handBody(k) {
  const F = [['index', 0.028, 0.04, 0.025, 0.021], ['middle', 0.009, 0.045, 0.028, 0.022], ['ring', -0.01, 0.042, 0.026, 0.021], ['pinky', -0.028, 0.034, 0.021, 0.019]];
  const arr = [
    ['wrist', null, [0, 0, 0], { ell: [0, -0.012, 0], rad: [0.032, 0.022, 0.02] }],
    ['palm', 'wrist', [0, -0.02, 0], { ell: [0, -0.042, 0], rad: [0.047, 0.05, 0.017] }],
  ];
  for (const [name, x, l1, l2, l3] of F) {
    const r = name === 'pinky' ? 0.0075 : 0.0088;
    arr.push([name + '1', 'palm', [x * k, -0.088, 0], { cap: [[0, 0, 0], [0, -l1, 0]], r }]);
    arr.push([name + '2', name + '1', [0, -l1, 0], { cap: [[0, 0, 0], [0, -l2, 0]], r: r * 0.92 }]);
    arr.push([name + '3', name + '2', [0, -l2, 0], { cap: [[0, 0, 0], [0, -l3, 0]], r: r * 0.85 }]);
  }
  arr.push(['thumb1', 'palm', [0.036 * k, -0.018, 0.01], { cap: [[0, 0, 0], [0.012 * k, -0.03, 0.006]], r: 0.012 }]);
  arr.push(['thumb2', 'thumb1', [0.012 * k, -0.03, 0.006], { cap: [[0, 0, 0], [0.006 * k, -0.028, 0.004]], r: 0.0095 }]);
  arr.push(['thumb3', 'thumb2', [0.006 * k, -0.028, 0.004], { cap: [[0, 0, 0], [0.003 * k, -0.024, 0.002]], r: 0.0085 }]);
  return arr;
}
const handCache = {};
export function handParts(side = 1) {
  return (handCache[side] ||= Object.fromEntries(handBody(side).map(([n, , , s]) => [n, partMesh(s)])));
}
/** 손 관절 회전(도) → 부위별 행렬. 왼손은 오른손 포즈를 거울로 */
export function handMatrices(pose, side = 1) {
  const out = {}, world = {};
  for (const [name, parent, off] of handBody(side)) {
    const r = pose[name] || [0, 0, 0];
    const local = M.mul(M.translate(...off), M.euler(side < 0 ? [r[0], -r[1], -r[2]] : r));
    world[name] = parent ? M.mul(world[parent], local) : local;
    out[name] = world[name];
  }
  return out;
}
export const HAND_JOINT_NAMES = {
  wrist: '손목', palm: '손바닥', thumb1: '엄지 뿌리', thumb2: '엄지 가운데', thumb3: '엄지 끝',
  index1: '검지 뿌리', index2: '검지 가운데', index3: '검지 끝', middle1: '중지 뿌리', middle2: '중지 가운데', middle3: '중지 끝',
  ring1: '약지 뿌리', ring2: '약지 가운데', ring3: '약지 끝', pinky1: '새끼 뿌리', pinky2: '새끼 가운데', pinky3: '새끼 끝',
};
const curl = (names, a, b, c) => Object.fromEntries(names.flatMap(n => [[n + '1', [a, 0, 0]], [n + '2', [b, 0, 0]], [n + '3', [c, 0, 0]]]));
const FOUR = ['index', 'middle', 'ring', 'pinky'];
const THUMB_IN = { thumb1: [-25, 0, -35], thumb2: [-20, 0, -30], thumb3: [-30, 0, 0] };
export const HAND_POSES = {
  '펼침': {},
  '편하게': { ...curl(FOUR, -18, -20, -12), thumb1: [-10, 0, -10] },
  '주먹': { ...curl(FOUR, -85, -95, -60), ...THUMB_IN },
  '가리키기': { ...curl(['middle', 'ring', 'pinky'], -85, -95, -60), ...THUMB_IN },
  '브이': { ...curl(['ring', 'pinky'], -85, -95, -60), index1: [0, 0, 9], middle1: [0, 0, -9], ...THUMB_IN },
  '엄지척': { ...curl(FOUR, -85, -95, -60), thumb1: [0, 0, 150], thumb2: [0, 0, 10] },
  '잡기 (C자)': { ...curl(FOUR, -35, -35, -25), thumb1: [-25, 0, -15], thumb2: [-15, 0, -10] },
  'OK': { ...curl(['middle', 'ring', 'pinky'], -8, -8, -5), index1: [-40, 0, 0], index2: [-60, 0, 0], index3: [-40, 0, 0], pinky1: [-12, 0, 8], thumb1: [-25, 0, -30], thumb2: [-25, 0, -20] },
  '손가락 벌리기': { index1: [0, 0, 12], middle1: [0, 0, 2], ring1: [0, 0, -8], pinky1: [0, 0, -18], thumb1: [0, 0, 25] },
};

export const POSES = {
  '차렷': {},
  'A 포즈': { upperArmL: [0, 0, 35], upperArmR: [0, 0, -35] },
  'T 포즈': { upperArmL: [0, 0, 88], upperArmR: [0, 0, -88] },
  '걷기': { upperArmL: [25, 0, 6], upperArmR: [-25, 0, -6], foreArmL: [-20, 0, 0], foreArmR: [-15, 0, 0], thighL: [-25, 0, 0], shinL: [12, 0, 0], thighR: [20, 0, 0], shinR: [25, 0, 0], footR: [-10, 0, 0], spine: [3, 0, 0] },
  '달리기': { spine: [12, 0, 0], chest: [6, 0, 0], upperArmL: [55, 0, 10], foreArmL: [-85, 0, 0], upperArmR: [-50, 0, -10], foreArmR: [-80, 0, 0], thighL: [-65, 0, 0], shinL: [75, 0, 0], thighR: [30, 0, 0], shinR: [70, 0, 0], head: [-8, 0, 0] },
  '의자에 앉기': { hips: [0, 0, 0], thighL: [-88, 0, 4], thighR: [-88, 0, -4], shinL: [88, 0, 0], shinR: [88, 0, 0], upperArmL: [-15, 0, 8], upperArmR: [-15, 0, -8], foreArmL: [-50, 0, 0], foreArmR: [-50, 0, 0] },
  '손 들기': { upperArmR: [0, 0, -160], foreArmR: [0, 0, -10], head: [-5, -10, 0] },
  '팔짱': { upperArmL: [-20, 0, 15], upperArmR: [-20, 0, -15], foreArmL: [-100, 40, 0], foreArmR: [-100, -40, 0] },
  '생각하기': { upperArmR: [-35, 0, -5], foreArmR: [-130, 0, 0], handR: [0, 0, 10], head: [8, 0, 8], upperArmL: [-25, 0, 20], foreArmL: [-95, -60, 0] },
  '점프': { spine: [-5, 0, 0], upperArmL: [0, 0, 150], upperArmR: [0, 0, -150], thighL: [-40, 0, 5], shinL: [80, 0, 0], thighR: [10, 0, -5], shinR: [60, 0, 0] },
  '주먹 내지르기': { chest: [0, -25, 0], upperArmR: [-85, 15, 0], foreArmR: [-5, 0, 0], upperArmL: [-60, 0, 30], foreArmL: [-110, 0, 0], thighL: [-30, 0, 0], shinL: [20, 0, 0], thighR: [15, 0, 0] },
};

// ---------------------------------------------------------------- 불러오기
const b64 = (u8) => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode(...u8.subarray(i, i + 0x8000)); return btoa(s); };
export const toBase64 = b64;
export const fromBase64 = (s) => Uint8Array.from(atob(s), c => c.charCodeAt(0));

export function parseOBJ(text) {
  const v = [], vn = [], vt = [];
  const pos = [], nrm = [], uv = [], idx = [];
  const map = new Map();
  let hasN = false;
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (!line || line[0] === '#') continue;
    const p = line.split(/\s+/);
    if (p[0] === 'v') v.push([+p[1], +p[2], +p[3]]);
    else if (p[0] === 'vn') vn.push([+p[1], +p[2], +p[3]]);
    else if (p[0] === 'vt') vt.push([+p[1], +p[2]]);
    else if (p[0] === 'f') {
      const ids = p.slice(1).map(tok => {
        if (map.has(tok)) return map.get(tok);
        const [a, b, c] = tok.split('/');
        const vi = +a < 0 ? v.length + +a : +a - 1;
        const P = v[vi] || [0, 0, 0];
        pos.push(...P);
        if (c) { const ni = +c < 0 ? vn.length + +c : +c - 1; nrm.push(...(vn[ni] || [0, 1, 0])); hasN = true; } else nrm.push(0, 0, 0);
        if (b) { const ti = +b < 0 ? vt.length + +b : +b - 1; uv.push(...(vt[ti] || [0, 0])); } else uv.push(0, 0);
        const id = pos.length / 3 - 1;
        map.set(tok, id);
        return id;
      });
      for (let i = 1; i < ids.length - 1; i++) idx.push(ids[0], ids[i], ids[i + 1]);
    }
  }
  const m = { pos: new Float32Array(pos), idx: new Uint32Array(idx), uv: new Float32Array(uv) };
  m.nrm = hasN ? new Float32Array(nrm) : computeNormals(m.pos, m.idx);
  return [{ mesh: m, matrix: M.ident(), color: [0.85, 0.85, 0.85], image: null }];
}

/** GLB 또는 GLTF(JSON, 데이터 포함) → [{ mesh, matrix, color, image }] */
export async function parseGLTF(bytes) {
  let json, bin = null;
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (dv.getUint32(0, true) === 0x46546c67) {
    let o = 12;
    while (o < bytes.length) {
      const len = dv.getUint32(o, true), type = dv.getUint32(o + 4, true);
      const chunk = bytes.subarray(o + 8, o + 8 + len);
      if (type === 0x4e4f534a) json = JSON.parse(new TextDecoder().decode(chunk));
      else if (type === 0x004e4942) bin = chunk;
      o += 8 + len;
    }
  } else json = JSON.parse(new TextDecoder().decode(bytes));
  const buffers = (json.buffers || []).map((b, i) => {
    if (!b.uri) return bin;
    if (b.uri.startsWith('data:')) return fromBase64(b.uri.split(',')[1]);
    throw new Error('외부 파일을 참조하는 .gltf 는 지원하지 않습니다. .glb 로 내보내 주세요');
  });
  const view = (i) => { const bv = json.bufferViews[i]; return { buf: buffers[bv.buffer], off: bv.byteOffset || 0, len: bv.byteLength, stride: bv.byteStride || 0 }; };
  const COMP = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 };
  const accessor = (i) => {
    const a = json.accessors[i], n = COMP[a.type], cnt = a.count;
    const out = new Float32Array(cnt * n);
    if (a.bufferView == null) return out;
    const bv = view(a.bufferView);
    const dvb = new DataView(bv.buf.buffer, bv.buf.byteOffset + bv.off + (a.byteOffset || 0));
    const size = { 5126: 4, 5123: 2, 5125: 4, 5121: 1, 5122: 2, 5120: 1 }[a.componentType];
    const stride = bv.stride || size * n;
    for (let k = 0; k < cnt; k++) for (let c = 0; c < n; c++) {
      const p = k * stride + c * size;
      let v;
      switch (a.componentType) {
        case 5126: v = dvb.getFloat32(p, true); break;
        case 5123: v = dvb.getUint16(p, true); if (a.normalized) v /= 65535; break;
        case 5125: v = dvb.getUint32(p, true); break;
        case 5121: v = dvb.getUint8(p); if (a.normalized) v /= 255; break;
        case 5122: v = dvb.getInt16(p, true); if (a.normalized) v = Math.max(v / 32767, -1); break;
        default: v = dvb.getInt8(p); if (a.normalized) v = Math.max(v / 127, -1);
      }
      out[k * n + c] = v;
    }
    return out;
  };
  const images = await Promise.all((json.images || []).map(async (im) => {
    try {
      let blob;
      if (im.bufferView != null) { const bv = view(im.bufferView); blob = new Blob([bv.buf.subarray(bv.off, bv.off + bv.len)], { type: im.mimeType || 'image/png' }); }
      else if (im.uri?.startsWith('data:')) blob = await (await fetch(im.uri)).blob();
      return blob ? await createImageBitmap(blob, { imageOrientation: 'flipY' }) : null;
    } catch { return null; }
  }));
  const out = [];
  const walk = (ni, parent) => {
    const n = json.nodes[ni];
    let local = M.ident();
    if (n.matrix) local = new Float32Array(n.matrix);
    else {
      const t = n.translation || [0, 0, 0], r = n.rotation || [0, 0, 0, 1], s = n.scale || [1, 1, 1];
      local = M.mul(M.translate(...t), M.mul(M.quat(r), M.scale(...s)));
    }
    const world = M.mul(parent, local);
    if (n.mesh != null) {
      for (const pr of json.meshes[n.mesh].primitives) {
        if (pr.mode != null && pr.mode !== 4) continue;
        const pos = accessor(pr.attributes.POSITION);
        const idx = pr.indices != null ? Uint32Array.from(accessor(pr.indices)) : Uint32Array.from({ length: pos.length / 3 }, (_, i) => i);
        const nrm = pr.attributes.NORMAL != null ? accessor(pr.attributes.NORMAL) : computeNormals(pos, idx);
        const uv = pr.attributes.TEXCOORD_0 != null ? accessor(pr.attributes.TEXCOORD_0) : null;
        const mat = pr.material != null ? json.materials[pr.material] : null;
        const pbr = mat?.pbrMetallicRoughness || {};
        const cf = pbr.baseColorFactor || [0.85, 0.85, 0.85, 1];
        const ti = pbr.baseColorTexture?.index;
        const image = ti != null ? images[json.textures[ti]?.source] || null : null;
        out.push({ mesh: { pos, nrm, idx, uv }, matrix: world, color: [cf[0], cf[1], cf[2]], image });
      }
    }
    for (const c of n.children || []) walk(c, world);
  };
  const scene = json.scenes?.[json.scene || 0];
  const roots = scene ? scene.nodes : json.nodes.map((_, i) => i).filter(i => !json.nodes.some(n => (n.children || []).includes(i)));
  for (const r of roots) walk(r, M.ident());
  if (!out.length) throw new Error('모델에서 삼각형 메시를 찾지 못했습니다');
  return out;
}

/** 모델 크기를 사람 키(1.7) 정도로 맞추고 바닥 가운데에 세운다 */
export function normalizeParts(parts, height = 1.7) {
  let x0 = Infinity, y0 = Infinity, z0 = Infinity, x1 = -Infinity, y1 = -Infinity, z1 = -Infinity;
  for (const p of parts) for (let i = 0; i < p.mesh.pos.length; i += 3) {
    const q = M.point(p.matrix, [p.mesh.pos[i], p.mesh.pos[i + 1], p.mesh.pos[i + 2]]);
    if (q[0] < x0) x0 = q[0]; if (q[0] > x1) x1 = q[0]; if (q[1] < y0) y0 = q[1]; if (q[1] > y1) y1 = q[1]; if (q[2] < z0) z0 = q[2]; if (q[2] > z1) z1 = q[2];
  }
  const s = height / Math.max(1e-6, Math.max(y1 - y0, (x1 - x0) * 0.6, (z1 - z0) * 0.6));
  const fix = M.mul(M.scale(s), M.translate(-(x0 + x1) / 2, -y0, -(z0 + z1) / 2));
  return parts.map(p => ({ ...p, matrix: M.mul(fix, p.matrix) }));
}
