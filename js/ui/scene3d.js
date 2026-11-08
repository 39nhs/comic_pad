// 3D 소재 창: 데생 인형/도형/3D 모델을 배치하고 포즈를 잡아 만화 레이어(색·음영·선화)로 합성
import { app } from '../app.js';
import * as ops from '../core/ops.js';
import { h, uid, pickFile, clamp } from '../core/util.js';
import { icon } from './icons.js';
import { slider, toggle, segmented, colorInput, select, button, row, section } from './controls.js';
import { M, mannequinMatrices, PRIMITIVES, POSES, JOINT_NAMES, HAND_POSES, HAND_JOINT_NAMES, BODY_DEFAULT, BODY_NAMES, BODY_PRESETS, parseGLTF, parseOBJ, normalizeParts, toBase64, fromBase64 } from '../three/engine.js';
import { Renderer3D } from '../three/render.js';
import { server } from '../core/io.js';
import { confirmDialog, form } from './dialogs.js';

export const DEFAULT_STYLE = {
  levels: 2, soft: 0.04, ambient: 0.35, lightAz: 40, lightEl: 45, shadowColor: '#9a93b0',
  line: true, lineWidth: 3, nThr: 0.75, dThr: 0.08, lineColor: '#111111',
  out: { color: true, shade: true, line: true }, bgDoc: true, grid: true,
};
const DEFAULT_CAMERA = { yaw: 25, pitch: 10, dist: 4.2, target: [0, 0.95, 0], fov: 40 };

// ---- 장면 저장/복원 ---------------------------------------------------------
export function serializeScene(s) {
  return {
    camera: { ...s.camera, target: [...s.camera.target] }, style: structuredClone(s.style),
    nodes: s.nodes.map(({ parts, ...n }) => structuredClone(n)),
  };
}

async function buildParts(n) {
  if (n.kind === 'prim') { const P = PRIMITIVES[n.prim]; return [{ mesh: P.make(), matrix: new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, P.y, 0, 1]), color: null, image: null }]; }
  if (n.kind === 'model' && n.src) {
    const bytes = fromBase64(n.src.data);
    const parts = n.src.type === 'obj' ? parseOBJ(new TextDecoder().decode(bytes)) : await parseGLTF(bytes);
    return normalizeParts(parts, n.src.height || 1.7);
  }
  return [];
}

export async function restoreScene(json) {
  const s = { camera: { ...DEFAULT_CAMERA, ...json?.camera }, style: { ...structuredClone(DEFAULT_STYLE), ...json?.style }, nodes: [] };
  for (const n of json?.nodes || []) { try { s.nodes.push({ ...n, parts: await buildParts(n) }); } catch (e) { app.toast(`3D 모델 복원 실패: ${n.name}`, 'warn'); } }
  return s;
}

function newNode(kind, extra) {
  return { id: uid('n'), kind, name: '물체', pos: [0, 0, 0], rot: [0, 0, 0], scale: 1, color: '#d8d8d8', visible: true, pose: {}, ...extra };
}

// ---- 창 ---------------------------------------------------------------------
let current = null;

export async function open3D(opts = {}) {
  if (current) return;
  const doc = app.doc;
  const folder = opts.folder || null;
  const scene = await restoreScene(opts.scene || folder?.meta?.scene3d || app.settings.last3D || null);
  if (!scene.nodes.length) scene.nodes.push(newNode('mannequin', { name: '데생 인형', color: '#e9dccf' }));
  let sel = scene.nodes[0]?.id || null;
  let joint = 'upperArmR';
  let mode = 'orbit';

  const root = h('div.s3d');
  const cv = h('canvas.s3d-canvas');
  const frame = h('div.s3d-frame', null, cv);
  const viewBox = h('div.s3d-view', null, frame);
  const side = h('div.s3d-side');
  const head = h('div.s3d-head', null,
    h('b', null, '3D 소재'),
    h('span.s3d-hint', null, '드래그: 시점 회전 · 두 손가락: 확대/이동 · 휠: 확대'),
    h('span.spacer'),
    button('만화에 넣기', () => insert(), 'primary'),
    button('닫기', () => close()));
  root.append(head, h('div.s3d-body', null, viewBox, side));
  document.body.append(root);
  current = root;

  let R;
  try { R = new Renderer3D(cv); } catch (e) { root.remove(); current = null; app.toast(e.message, 'error'); return; }

  // 원고 비치기용 미리보기
  const bgURL = (() => { const s = Math.min(1, 900 / Math.max(doc.width, doc.height)); return app.compositor.flatten(doc, { scale: s }).toDataURL('image/jpeg', 0.8); })();

  // ---- 그리기 ----
  let queued = false, pw = 0, ph = 0;
  const layout = () => {
    const r = viewBox.getBoundingClientRect();
    const a = doc.width / doc.height;
    let w = r.width - 16, hh = w / a;
    if (hh > r.height - 16) { hh = r.height - 16; w = hh * a; }
    frame.style.width = Math.max(50, w) + 'px';
    frame.style.height = Math.max(50, hh) + 'px';
    frame.style.backgroundImage = scene.style.bgDoc ? `url(${bgURL})` : 'none';
    const dpr = Math.min(2, devicePixelRatio || 1);
    pw = Math.min(2048, Math.round(w * dpr)); ph = Math.min(2048, Math.round(hh * dpr));
    draw();
  };
  const draw = () => {
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => {
      queued = false;
      if (!pw) return;
      try { R.render(scene, pw, ph, 'preview', scene.style, { grid: scene.style.grid, lineScale: pw / doc.width }); } catch (e) { console.error(e); }
    });
  };
  const ro = new ResizeObserver(layout);
  ro.observe(viewBox);

  // ---- 조작 ----
  const pts = new Map();
  let last = null;
  const selNode = () => scene.nodes.find(n => n.id === sel);
  cv.addEventListener('pointerdown', (e) => {
    cv.setPointerCapture(e.pointerId);
    pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
    last = pinchState();
  });
  const pinchState = () => {
    const p = [...pts.values()];
    if (p.length >= 2) return { two: true, d: Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y), cx: (p[0].x + p[1].x) / 2, cy: (p[0].y + p[1].y) / 2 };
    if (p.length === 1) return { two: false, x: p[0].x, y: p[0].y };
    return null;
  };
  const cam = scene.camera;
  const camAxes = () => {
    const y = cam.yaw * Math.PI / 180;
    return { right: [Math.cos(y), 0, -Math.sin(y)], fwd: [-Math.sin(y), 0, -Math.cos(y)] };
  };
  // ---- 손발 끌기 (IK): 손이나 발을 끌면 팔·다리 관절이 알아서 따라 굽혀짐 ----
  let ikTarget = null;
  const IK_CHAINS = {
    arm: (s) => ({ end: 'hand' + s, params: [['upperArm' + s, 0], ['upperArm' + s, 1], ['upperArm' + s, 2], ['foreArm' + s, 0]], limit: { ['foreArm' + s]: [-160, 0] } }),
    leg: (s) => ({ end: 'foot' + s, params: [['thigh' + s, 0], ['thigh' + s, 1], ['thigh' + s, 2], ['shin' + s, 0]], limit: { ['shin' + s]: [0, 160] } }),
  };
  const chainFor = (j) => {
    const s = j.endsWith('L') ? 'L' : j.endsWith('R') ? 'R' : null;
    if (!s) return null;
    if (/^(hand|foreArm|upperArm|shoulder)/.test(j)) return IK_CHAINS.arm(s);
    if (/^(foot|shin|thigh)/.test(j)) return IK_CHAINS.leg(s);
    return null;
  };
  const endPos = (n, pose, end) => {
    const W = M.mul(M.trs(n.pos, n.rot, n.scale), mannequinMatrices(pose, n.body)[end]);
    return [W[12], W[13], W[14]];
  };
  const solveIK = (n, ch, target) => {
    const pose = n.pose;
    const err = () => { const p = endPos(n, pose, ch.end); return (p[0] - target[0]) ** 2 + (p[1] - target[1]) ** 2 + (p[2] - target[2]) ** 2; };
    let best = err();
    for (const step of [12, 6, 3, 1.5, 0.75]) {
      for (let pass = 0; pass < 4; pass++) {
        let improved = false;
        for (const [jn, ax] of ch.params) {
          const r = pose[jn] = pose[jn] || [0, 0, 0];
          const lim = ax === 0 && ch.limit[jn] ? ch.limit[jn] : [-180, 180];
          for (const d of [step, -step]) {
            const old = r[ax];
            r[ax] = clamp(old + d, lim[0], lim[1]);
            const e = err();
            if (e < best - 1e-9) { best = e; improved = true; } else r[ax] = old;
          }
        }
        if (!improved) break;
      }
    }
  };
  const camUp = () => {
    const d = Math.PI / 180, cp = Math.cos(cam.pitch * d);
    const f = [-cp * Math.sin(cam.yaw * d), -Math.sin(cam.pitch * d), -cp * Math.cos(cam.yaw * d)];
    const r = camAxes().right;
    return [r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0]];
  };

  const pan = (dx, dy) => {
    const k = cam.dist * 0.0018, { right } = camAxes();
    cam.target[0] -= right[0] * dx * k; cam.target[2] -= right[2] * dx * k;
    cam.target[1] += dy * k;
  };
  cv.addEventListener('pointermove', (e) => {
    if (!pts.has(e.pointerId)) return;
    pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const now = pinchState();
    if (!now || !last) { last = now; return; }
    if (now.two && last.two) {
      cam.dist = clamp(cam.dist * last.d / Math.max(1, now.d), 0.3, 60);
      pan(now.cx - last.cx, now.cy - last.cy);
    } else if (!now.two && !last.two) {
      const dx = now.x - last.x, dy = now.y - last.y;
      const n = selNode();
      if (mode === 'orbit' || !n || e.buttons === 4 || e.buttons === 2) {
        if (e.buttons === 4 || e.buttons === 2 || e.shiftKey) pan(dx, dy);
        else { cam.yaw -= dx * 0.4; cam.pitch = clamp(cam.pitch + dy * 0.3, -89, 89); }
      } else if (mode === 'move') {
        const k = cam.dist * 0.0018, { right, fwd } = camAxes();
        if (e.shiftKey) n.pos[1] -= dy * k;
        else { n.pos[0] += (right[0] * dx - fwd[0] * dy) * k; n.pos[2] += (right[2] * dx - fwd[2] * dy) * k; }
      } else if (mode === 'turn') {
        n.rot[1] += dx * 0.5; n.rot[0] = clamp(n.rot[0] + dy * 0.5, -180, 180);
      } else if (mode === 'ik' && n.kind === 'mannequin' && chainFor(joint)) {
        const ch = chainFor(joint), k = cam.dist * 0.0018, { right } = camAxes(), up = camUp();
        ikTarget ||= endPos(n, n.pose, ch.end);
        for (let i = 0; i < 3; i++) ikTarget[i] += right[i] * dx * k - up[i] * dy * k;
        solveIK(n, ch, ikTarget);
      } else if (mode === 'pose' && (n.kind === 'mannequin' || n.kind === 'hand')) {
        const r = n.pose[joint] = n.pose[joint] || [0, 0, 0];
        r[2] = clamp(r[2] - dx * 0.5, -180, 180); r[0] = clamp(r[0] + dy * 0.5, -180, 180);
      }
      syncSliders();
    }
    last = now;
    draw();
  });
  const up = (e) => { pts.delete(e.pointerId); last = pinchState(); ikTarget = null; };
  cv.addEventListener('pointerup', up);
  cv.addEventListener('pointercancel', up);
  cv.addEventListener('wheel', (e) => { e.preventDefault(); cam.dist = clamp(cam.dist * (e.deltaY > 0 ? 1.1 : 1 / 1.1), 0.3, 60); syncSliders(); draw(); }, { passive: false });
  cv.addEventListener('contextmenu', (e) => e.preventDefault());

  // ---- 옆 패널 ----
  const syncSliders = () => side.querySelectorAll('.ctl').forEach(c => c.sync?.());
  const S = (label, o, key, min, max, step = 1, fmt, i) => slider(label, {
    min, max, step, format: fmt,
    get: () => (i == null ? o()?.[key] : o()?.[key]?.[i]) ?? 0,
    set: v => { const t = o(); if (!t) return; if (i == null) t[key] = v; else t[key][i] = v; draw(); },
  });
  const renderSide = () => {
    side.innerHTML = '';
    const n = selNode();
    const add = (kind, extra) => { const nn = newNode(kind, extra); scene.nodes.push(nn); sel = nn.id; renderSide(); draw(); return nn; };
    // 손: 사람 크기에 맞춰 6배로, 카메라를 손 쪽으로
    const addHand = (side) => {
      joint = 'index1';
      // 손가락이 위를 향하게 (Z 축 180° 회전: 거울이 아니라 돌림이라 오른손은 그대로 오른손)
      const nn = add('hand', { name: side > 0 ? '오른손' : '왼손', side, color: '#e9dccf', scale: 6, rot: [0, 0, 180], pos: [scene.nodes.length * 0.7, 0.5, 0] });
      cam.target = [nn.pos[0], nn.pos[1] + 0.6, nn.pos[2]];
      cam.dist = Math.min(cam.dist, 3);
      syncSliders(); draw();
    };
    const list = h('div.s3d-list');
    for (const x of scene.nodes) {
      const it = h(`div.s3d-item${x.id === sel ? '.on' : ''}`, null,
        h('button.icon-btn', { type: 'button', html: icon(x.visible !== false ? 'eye' : 'eyeOff', 16), onclick: (e) => { e.stopPropagation(); x.visible = x.visible === false; renderSide(); draw(); } }),
        h('span', null, x.name),
        h('button.icon-btn', { type: 'button', title: '복제', html: icon('copy', 16), onclick: async (e) => { e.stopPropagation(); const c = { ...structuredClone({ ...x, parts: undefined }), id: uid('n'), name: x.name + ' 2', parts: x.parts }; c.pos[0] += 0.6; scene.nodes.push(c); sel = c.id; renderSide(); draw(); } }),
        h('button.icon-btn', { type: 'button', title: '삭제', html: icon('trash', 16), onclick: (e) => { e.stopPropagation(); scene.nodes = scene.nodes.filter(y => y !== x); if (sel === x.id) sel = scene.nodes[0]?.id; renderSide(); draw(); } }));
      it.onclick = () => { sel = x.id; renderSide(); };
      list.append(it);
    }
    side.append(
      section('추가',
        row(button(icon('star', 16) + ' 데생 인형', () => add('mannequin', { name: '데생 인형', color: '#e9dccf', pos: [scene.nodes.length * 0.7, 0, 0] })),
          button('손 (오른손)', () => addHand(1)), button('손 (왼손)', () => addHand(-1))),
        h('div.s3d-prims', null, ...Object.entries(PRIMITIVES).map(([k, P]) => button(P.name, async () => { const nn = add('prim', { prim: k, name: P.name }); nn.parts = await buildParts(nn); draw(); }))),
        row(button(icon('folder', 16) + ' 모델 파일 (.glb .gltf .obj)', () => importFile()), button('에셋 폴더에서', () => importAsset())),
      ),
      section('물체', list),
      n ? section(`선택: ${n.name}`,
        segmented([['orbit', '시점'], ['move', '이동'], ['turn', '회전'], ...(n.kind === 'mannequin' || n.kind === 'hand' ? [['pose', '포즈']] : []), ...(n.kind === 'mannequin' ? [['ik', '손발 끌기']] : [])], () => mode, v => { mode = v; if (v === 'ik' && !chainFor(joint)) { joint = 'handR'; syncSliders(); } }),
        S('좌우 (X)', selNode, 'pos', -10, 10, 0.01, v => v.toFixed(2), 0),
        S('높이 (Y)', selNode, 'pos', -3, 6, 0.01, v => v.toFixed(2), 1),
        S('앞뒤 (Z)', selNode, 'pos', -10, 10, 0.01, v => v.toFixed(2), 2),
        S('회전 X', selNode, 'rot', -180, 180, 1, v => v + '°', 0),
        S('회전 Y', selNode, 'rot', -180, 180, 1, v => v + '°', 1),
        S('회전 Z', selNode, 'rot', -180, 180, 1, v => v + '°', 2),
        S('크기', selNode, 'scale', 0.05, 10, 0.01, v => v.toFixed(2)),
        row(colorInput('색', () => n.color, v => { n.color = v; n.tint = true; draw(); }), n.kind === 'model' ? toggle('모델 원래 색 무시', () => !!n.tint, v => { n.tint = v; draw(); }) : null),
        n.kind === 'model' ? row(button('이름 바꾸기', () => { const t = prompt('이름', n.name); if (t) { n.name = t; renderSide(); } })) : null,
      ) : null,
      n?.kind === 'hand' ? section('손 포즈',
        select('마디', Object.entries(HAND_JOINT_NAMES), () => (HAND_JOINT_NAMES[joint] ? joint : (joint = 'index1')), v => { joint = v; syncSliders(); }),
        ...[0, 1, 2].map(i => slider(['굽힘 (X)', '비틀기 (Y)', '옆으로 벌림 (Z)'][i], {
          min: -180, max: 180, step: 1, format: v => v + '°',
          get: () => (n.pose[joint] || [0, 0, 0])[i],
          set: v => { (n.pose[joint] = n.pose[joint] || [0, 0, 0])[i] = v; draw(); },
        })),
        h('div.s3d-prims', null, ...Object.keys(HAND_POSES).map(k => button(k, () => { n.pose = structuredClone(HAND_POSES[k]); syncSliders(); draw(); }))),
        row(button('마디 초기화', () => { delete n.pose[joint]; syncSliders(); draw(); }), button('왼손/오른손 바꾸기', () => { n.side = -(n.side || 1); n.name = n.side > 0 ? '오른손' : '왼손'; renderSide(); draw(); }), button('사진에서 손 모양 (AI)…', () => photoPose(n, 'hand'))),
        h('div.help', null, '"포즈" 모드에서 화면을 끌면 고른 마디가 굽혀집니다. 굽힘이 음수면 손바닥 쪽으로 접힙니다.'),
      ) : null,
      n?.kind === 'mannequin' ? section('포즈',
        select('관절', Object.entries(JOINT_NAMES), () => (JOINT_NAMES[joint] ? joint : (joint = 'upperArmR')), v => { joint = v; syncSliders(); }),
        ...[0, 1, 2].map(i => slider(['앞뒤로 굽힘 (X)', '비틀기 (Y)', '옆으로 벌림 (Z)'][i], {
          min: -180, max: 180, step: 1, format: v => v + '°',
          get: () => (n.pose[joint] || [0, 0, 0])[i],
          set: v => { (n.pose[joint] = n.pose[joint] || [0, 0, 0])[i] = v; draw(); },
        })),
        h('div.s3d-prims', null, ...Object.keys(POSES).map(k => button(k, () => { n.pose = structuredClone(POSES[k]); syncSliders(); draw(); }))),
        row(button('관절 초기화', () => { delete n.pose[joint]; syncSliders(); draw(); }), button('좌우 뒤집기', () => { n.pose = mirrorPose(n.pose); syncSliders(); draw(); })),
        row(button('BVH 포즈 파일 열기…', () => loadBVH(n)), button('사진에서 자세 (AI)…', () => photoPose(n, 'pose'))),
        n.bvh ? slider(`BVH 프레임 (${n.bvh.poses.length}장)`, { min: 0, max: n.bvh.poses.length - 1, step: 1, get: () => n.bvh.at || 0, set: v => { n.bvh.at = v; n.pose = structuredClone(n.bvh.poses[v]); draw(); }, format: v => `${v + 1}` }) : null,
        n.bvh ? h('div.help', null, `${n.bvh.name} · 관절 ${n.bvh.matched}개 맞춤 · ${n.bvh.fps.toFixed(1)}fps`) : null,
        h('div.help', null, '"포즈" 모드: 화면을 끌면 고른 관절이 돌아갑니다. "손발 끌기" 모드: 관절에서 손·발(또는 팔·다리 마디)을 고르고 끌면 팔꿈치·무릎까지 알아서 굽혀집니다.'),
      ) : null,
      n?.kind === 'mannequin' ? section('체형',
        h('div.s3d-prims', null, ...Object.keys(BODY_PRESETS).map(k => button(k, () => { n.body = { ...BODY_DEFAULT, ...BODY_PRESETS[k] }; renderSide(); draw(); }))),
        ...Object.keys(BODY_DEFAULT).map(k => slider(BODY_NAMES[k], {
          min: k === 'head' ? 0.6 : 0.4, max: k === 'head' ? 2.5 : 1.8, step: 0.01, format: v => Math.round(v * 100) + '%',
          get: () => n.body?.[k] ?? 1,
          set: v => { n.body = { ...BODY_DEFAULT, ...(n.body || {}), [k]: v }; draw(); },
        })),
      ) : null,
      section('카메라',
        slider('화각 (원근감)', { min: 8, max: 100, step: 1, get: () => cam.fov, set: v => { cam.fov = v; draw(); }, format: v => v + '°' }),
        slider('거리', { min: 0.3, max: 40, step: 0.05, log: true, get: () => cam.dist, set: v => { cam.dist = v; draw(); }, format: v => v.toFixed(2) }),
        slider('좌우 각도', { min: -180, max: 180, step: 1, get: () => ((cam.yaw % 360) + 540) % 360 - 180, set: v => { cam.yaw = v; draw(); }, format: v => Math.round(v) + '°' }),
        slider('높낮이 (부감/앙각)', { min: -89, max: 89, step: 1, get: () => cam.pitch, set: v => { cam.pitch = v; draw(); }, format: v => Math.round(v) + '°' }),
        h('div.s3d-prims', null,
          button('정면', () => { Object.assign(cam, { yaw: 0, pitch: 0 }); syncSliders(); draw(); }),
          button('옆', () => { Object.assign(cam, { yaw: 90, pitch: 0 }); syncSliders(); draw(); }),
          button('부감', () => { Object.assign(cam, { pitch: 45 }); syncSliders(); draw(); }),
          button('앙각', () => { Object.assign(cam, { pitch: -25 }); syncSliders(); draw(); }),
          button('선택 물체 보기', () => { const x = selNode(); if (x) { cam.target = [x.pos[0], x.pos[1] + 0.9 * x.scale, x.pos[2]]; draw(); } }),
          button('초기화', () => { Object.assign(cam, structuredClone(DEFAULT_CAMERA)); syncSliders(); draw(); })),
      ),
      section('만화 스타일',
        slider('음영 단계', { min: 0, max: 4, step: 1, get: () => scene.style.levels, set: v => { scene.style.levels = v; draw(); }, format: v => (v ? v + '단' : '부드럽게') }),
        slider('경계 부드러움', { min: 0, max: 0.5, step: 0.01, get: () => scene.style.soft, set: v => { scene.style.soft = v; draw(); } }),
        slider('밝기 (환경광)', { min: 0, max: 0.9, step: 0.01, get: () => scene.style.ambient, set: v => { scene.style.ambient = v; draw(); } }),
        slider('빛 방향 (좌우)', { min: -180, max: 180, step: 1, get: () => scene.style.lightAz, set: v => { scene.style.lightAz = v; draw(); }, format: v => v + '°' }),
        slider('빛 높이', { min: -30, max: 90, step: 1, get: () => scene.style.lightEl, set: v => { scene.style.lightEl = v; draw(); }, format: v => v + '°' }),
        colorInput('그림자 색', () => scene.style.shadowColor, v => { scene.style.shadowColor = v; draw(); }),
        toggle('선화', () => scene.style.line, v => { scene.style.line = v; draw(); }),
        slider('선 굵기 (원고 px)', { min: 1, max: 20, step: 0.5, get: () => scene.style.lineWidth, set: v => { scene.style.lineWidth = v; draw(); } }),
        slider('안쪽 선 양', { min: 0, max: 1, step: 0.01, get: () => 1 - scene.style.nThr, set: v => { scene.style.nThr = 1 - v; draw(); }, format: v => Math.round(v * 100) + '%' }),
        slider('겹침 선 감도', { min: 0.01, max: 0.5, step: 0.01, get: () => 0.51 - scene.style.dThr, set: v => { scene.style.dThr = 0.51 - v; draw(); }, format: v => Math.round(v * 200) + '%' }),
        colorInput('선 색', () => scene.style.lineColor, v => { scene.style.lineColor = v; draw(); }),
        row(toggle('원고 비치기', () => scene.style.bgDoc, v => { scene.style.bgDoc = v; layout(); }), toggle('바닥 격자', () => scene.style.grid, v => { scene.style.grid = v; draw(); })),
      ),
      section('애니메이션 프레임으로',
        h('div.help', null, '지금 장면을 여러 장 그려 애니메이션 폴더로 넣습니다: BVH 동작을 따라가거나, 카메라를 빙 돌리거나.'),
        row(button('애니 프레임 만들기…', () => toAnimFrames())),
      ),
      section('만화에 넣을 레이어',
        row(toggle('색', () => scene.style.out.color, v => { scene.style.out.color = v; }), toggle('음영(곱하기)', () => scene.style.out.shade, v => { scene.style.out.shade = v; }), toggle('선화', () => scene.style.out.line, v => { scene.style.out.line = v; })),
        row(toggle('퍼스 자도 같이 만들기', () => scene.style.out.rulers !== false, v => { scene.style.out.rulers = v; })),
        row(button(icon('check', 16) + ' 만화에 넣기', () => insert(), 'primary'), button('퍼스 자만 만들기', () => rulersFromCamera())),
        h('div.help', null, '원고 크기 그대로 "3D" 폴더에 색 · 음영 · 선화 레이어로 들어갑니다. 폴더 안 레이어를 고르고 다시 3D 창을 열면 이어서 고칠 수 있습니다.'),
      ),
    );
  };

  const importBytes = async (name, bytes) => {
    const isObj = /\.obj$/i.test(name);
    try {
      const nn = newNode('model', { name: name.replace(/\.[^.]+$/, ''), src: { type: isObj ? 'obj' : 'glb', name, data: toBase64(bytes) }, pos: [scene.nodes.length * 0.7, 0, 0] });
      nn.parts = await buildParts(nn);
      scene.nodes.push(nn);
      sel = nn.id;
      renderSide(); draw();
      if (bytes.length > 15e6) app.toast('큰 모델은 작품 파일 크기가 커집니다', 'warn');
    } catch (e) { app.toast('모델을 읽지 못했습니다: ' + e.message, 'error'); }
  };
  const importFile = async () => {
    const f = await pickFile('.glb,.gltf,.obj');
    if (f) await app.task('모델 불러오기', async () => importBytes(f.name, new Uint8Array(await f.arrayBuffer())));
  };
  const importAsset = async () => {
    const items = (await server.list('assets')).filter(i => /\.(glb|gltf|obj)$/i.test(i.name));
    if (!items.length) { app.toast('data/assets 폴더에 .glb/.gltf/.obj 파일을 넣어 주세요'); return; }
    const v = await app.ui.form('에셋 폴더의 3D 모델', [{ key: 'n', label: '파일', type: 'select', options: items.map(i => i.name), value: items[0].name }]);
    if (!v) return;
    await app.task('모델 불러오기', async () => importBytes(v.n, new Uint8Array(await (await server.get('assets', v.n)).arrayBuffer())));
  };

  // ---- 3D 카메라 → 퍼스 자 ----
  // 물체의 세 방향(가로·깊이·세로)이 화면에서 모이는 점(소실점)을 계산해 원고에 투시 자로 넣는다
  const rulersFromCamera = (silent = false) => {
    const { view, proj } = R.camera(cam, doc.width / doc.height);
    const VP = M.mul(proj, view);
    const n = selNode();
    const rot = n ? M.euler(n.rot || [0, 0, 0]) : M.ident();   // 고른 물체가 돌아가 있으면 그 방향 기준
    const vps = [];
    for (const axis of [0, 1, 2]) {
      const d = [rot[axis * 4], rot[axis * 4 + 1], rot[axis * 4 + 2]];
      const x = VP[0] * d[0] + VP[4] * d[1] + VP[8] * d[2], y = VP[1] * d[0] + VP[5] * d[1] + VP[9] * d[2], w = VP[3] * d[0] + VP[7] * d[1] + VP[11] * d[2];
      if (Math.abs(w) < 1e-4) continue;                 // 화면과 나란한 방향: 소실점 없음
      const nx = x / w, ny = y / w;
      if (Math.hypot(nx, ny) > 40) continue;              // 너무 멀면 사실상 평행
      vps.push({ x: (nx * 0.5 + 0.5) * doc.width, y: (0.5 - ny * 0.5) * doc.height, axis });
    }
    if (!vps.length) { if (!silent) app.toast('지금 시점에서는 소실점이 없습니다 (정면 평행 투영에 가까움)'); return 0; }
    const before = structuredClone(doc.rulers);
    doc.rulers.push({ type: 'perspective', vps: vps.map(({ x, y }) => ({ x, y })), from3D: true });
    app.settings.showRulers = true;
    app.history.push({ label: '3D 퍼스 자', undo: () => { doc.rulers = structuredClone(before); app.requestRender(); }, redo: () => { doc.rulers = [...structuredClone(before), { type: 'perspective', vps: vps.map(({ x, y }) => ({ x, y })), from3D: true }]; app.requestRender(); } });
    app.markModified();
    app.requestRender();
    if (!silent) app.toast(`${vps.length}점 투시 자를 만들었습니다${n ? ` ("${n.name || '물체'}" 방향 기준)` : ''}. 자 도구로 확인하세요`);
    return vps.length;
  };

  // ---- 사진 → 자세 (내장 AI) ----
  const photoPose = async (n, kind) => {
    const { poseFromPhoto } = await import('../ai/pose.js');
    const pose = await poseFromPhoto(kind);
    if (!pose) return;
    n.pose = pose;
    renderSide(); draw();
    app.toast(kind === 'pose' ? '사진의 자세를 인형에 입혔습니다 (관절 슬라이더로 다듬으세요)' : '사진의 손 모양을 입혔습니다');
  };

  // ---- BVH 모션 캡처 ----
  const loadBVH = async (n) => {
    const f = await pickFile('.bvh');
    if (!f) return;
    try {
      const { bvhPoses } = await import('../three/bvh.js');
      const r = bvhPoses(await f.text());
      n.bvh = { name: f.name, poses: r.poses, fps: r.fps, matched: r.matched, at: 0 };
      n.pose = structuredClone(r.poses[0]);
      renderSide(); draw();
      app.toast(`BVH: ${r.frames}프레임 → ${r.poses.length}개 포즈 (관절 ${r.matched}개 맞춤)`);
    } catch (e) { app.toast('BVH 를 읽지 못했습니다: ' + e.message, 'error'); }
  };

  // ---- 3D → 애니메이션 프레임 ----
  const toAnimFrames = async () => {
    const withBvh = scene.nodes.filter(x => x.bvh);
    const v = await form('애니메이션 프레임 만들기', [
      { key: 'count', label: '장 수', type: 'number', value: withBvh.length ? Math.min(48, withBvh[0].bvh.poses.length) : 24, min: 2, max: 240 },
      { key: 'bvh', label: withBvh.length ? `BVH 동작 따라가기 (${withBvh.map(x => x.name).join(', ')})` : 'BVH 동작 (불러온 BVH 가 없음)', type: 'checkbox', value: withBvh.length > 0 },
      { key: 'orbit', label: '카메라를 돌리는 각도 (0 = 고정)', type: 'number', value: withBvh.length ? 0 : 90 },
      { key: 'what', label: '그릴 것', type: 'segment', options: [['line', '선화만'], ['color', '색+선화']], value: 'line' },
      { key: 'info', type: 'info', label: '프레임마다 원고 크기 그대로 한 장씩 그려 "3D 애니" 폴더에 넣습니다. 위에 따라 그리거나 그대로 써도 됩니다.' },
    ], { okLabel: '만들기' });
    if (!v) return;
    const N = Math.max(2, Math.min(240, Math.round(v.count)));
    const s = Math.min(1, 4096 / Math.max(doc.width, doc.height));
    const W = Math.round(doc.width * s), H = Math.round(doc.height * s);
    const yaw0 = cam.yaw, saved = scene.nodes.map(x => structuredClone(x.pose || {}));
    const frames = [];
    await app.task(`3D → ${N}장`, async () => {
      for (let i = 0; i < N; i++) {
        const t = N === 1 ? 0 : i / (N - 1);
        if (v.bvh) for (const x of withBvh) x.pose = structuredClone(x.bvh.poses[Math.min(x.bvh.poses.length - 1, Math.round(t * (x.bvh.poses.length - 1)))]);
        cam.yaw = yaw0 + (v.orbit || 0) * t;
        const st = { ...scene.style, line: true };
        const c = document.createElement('canvas'); c.width = doc.width; c.height = doc.height;
        const cx = c.getContext('2d');
        if (v.what === 'color') { cx.drawImage(R.snapshot(scene, W, H, 'color', st, { lineScale: s }), 0, 0, doc.width, doc.height); cx.globalCompositeOperation = 'multiply'; cx.drawImage(R.snapshot(scene, W, H, 'shade', st, { lineScale: s }), 0, 0, doc.width, doc.height); cx.globalCompositeOperation = 'source-over'; }
        cx.drawImage(R.snapshot(scene, W, H, 'line', st, { lineScale: s }), 0, 0, doc.width, doc.height);
        frames.push(c);
        if (i % 4 === 3) await new Promise(r => setTimeout(r, 0));
      }
    });
    cam.yaw = yaw0; scene.nodes.forEach((x, k) => { x.pose = saved[k]; }); draw();
    app.history.batch('3D 애니 프레임', () => {
      const f = ops.addLayer({ name: '3D 애니', kind: 'folder', parent: null, meta: { anim: true } }, doc.layers.length);
      frames.forEach((c, i) => { const l = ops.addLayer({ name: String(i + 1), parent: f.id }, doc.indexOf(f)); l.ctx.drawImage(c, 0, 0); l.touch(); });
      doc.anim.fps = Math.round(withBvh[0]?.bvh.fps && v.bvh ? Math.min(24, Math.max(6, withBvh[0].bvh.fps * withBvh[0].bvh.poses.length / N)) : 12);
      doc.setActive(doc.childrenOf(f)[0] || f);
    });
    app.toast(`"3D 애니" 폴더에 ${N}장을 넣었습니다. 애니 패널에서 재생해 보세요`);
  };

  // ---- 만화에 넣기 ----
  const insert = async () => {
    const out = scene.style.out;
    if (!out.color && !out.shade && !out.line) { app.toast('넣을 레이어를 하나 이상 고르세요'); return; }
    await app.task('3D 합성', async () => {
      const s = Math.min(1, 8192 / Math.max(doc.width, doc.height));
      const W = Math.round(doc.width * s), H = Math.round(doc.height * s);
      const snap = (what) => R.snapshot(scene, W, H, what, { ...scene.style, line: true }, { lineScale: s });
      const imgs = {};
      if (out.color) imgs.color = snap('color');
      if (out.shade) imgs.shade = snap('shade');
      if (out.line) imgs.line = snap('line');
      const json = serializeScene(scene);
      app.history.batch('3D 합성', () => {
        let f = folder && doc.layers.includes(folder) ? folder : null;
        if (f) {
          for (const c of doc.childrenOf(f)) ops.deleteLayer(c);
          ops.setLayerProps(f, { meta: { ...f.meta, scene3d: json } }, '3D 장면');
        } else f = ops.addLayer({ name: '3D', kind: 'folder', parent: null, meta: { scene3d: json } }, doc.layers.length);
        const put = (name, img, blend) => {
          const l = ops.addLayer({ name, parent: f.id, blend }, doc.indexOf(f));
          l.ctx.imageSmoothingQuality = 'high';
          l.ctx.drawImage(img, 0, 0, doc.width, doc.height);
          l.touch();
        };
        if (imgs.color) put('3D 색', imgs.color, 'source-over');
        if (imgs.shade) put('3D 음영', imgs.shade, 'multiply');
        if (imgs.line) put('3D 선화', imgs.line, 'source-over');
        doc.setActive(f);
      });
      app.settings.last3D = slim(json);
      app.saveSettingsSoon();
    });
    if (scene.style.out.rulers !== false) rulersFromCamera(true);
    close(true);
    app.toast('3D 폴더에 넣었습니다. 선화 레이어 위에 그려서 다듬어 보세요');
  };

  const close = async (done) => {
    if (!done && !(await confirmDialog('3D 창을 닫을까요? (넣지 않은 변경은 마지막 장면으로만 기억됩니다)', '닫기'))) return;
    app.settings.last3D = slim(serializeScene(scene));
    app.saveSettingsSoon();
    ro.disconnect();
    root.remove();
    current = null;
  };

  // 모델 파일을 창에 끌어다 놓기
  root.addEventListener('dragover', (e) => { e.preventDefault(); e.stopPropagation(); });
  root.addEventListener('drop', async (e) => {
    e.preventDefault(); e.stopPropagation();
    for (const f of e.dataTransfer?.files || []) {
      if (/\.(glb|gltf|obj)$/i.test(f.name)) await app.task('모델 불러오기', async () => importBytes(f.name, new Uint8Array(await f.arrayBuffer())));
      else app.toast('.glb / .gltf / .obj 파일만 넣을 수 있습니다', 'warn');
    }
  });

  renderSide();
  layout();
}

// 설정 파일에는 큰 모델 데이터를 넣지 않는다 (인형·도형·카메라·스타일만)
function slim(j) { return { ...j, nodes: j.nodes.filter(n => n.kind !== 'model') }; }

function mirrorPose(p) {
  const out = {};
  for (const [k, v] of Object.entries(p)) {
    const m = k.endsWith('L') ? k.slice(0, -1) + 'R' : k.endsWith('R') ? k.slice(0, -1) + 'L' : k;
    out[m] = [v[0], -v[1], -v[2]];
  }
  return out;
}

export function register3DCommands() {
  app.registerCommand('3d.open', {
    label: '3D 소재 (데생 인형·모델 합성)…',
    run: () => {
      const doc = app.doc, a = doc.active;
      let f = a?.meta?.scene3d ? a : null;
      if (!f && a) { const p = doc.parentOf(a); const pf = p && doc.byId(p); if (pf?.meta?.scene3d) f = pf; }
      return open3D({ folder: f });
    },
  });
  app.addMenuItem('comic', { sep: true });
  app.addMenuItem('comic', { command: '3d.open' });
  app.addMenuItem('asset', { command: '3d.open' });
  app.registerQuickAction('3d', { icon: 'cube', label: '3D 소재', run: () => app.run('3d.open') });
}
