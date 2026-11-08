// 버튜버 모드: 그린 캐릭터의 부위(레이어)를 정하고, 웹캠 얼굴·마이크로 움직여 방송 창·녹화로 내보낸다.
import { app } from '../app.js';
import { h, createCanvas, pickFile, downloadBlob, safeName, fileStamp, canvasToBlob } from '../core/util.js';
import { openModal } from './dialogs.js';
import { slider, toggle, segmented, button, row, section } from './controls.js';
import { server } from '../core/io.js';
import * as V from '../vtuber/rig.js';

const MAX_SIDE = 1600;                 // 리그 그림 최대 크기 (방송용으로 충분, 메모리 아낌)
const BGS = { green: '#00ff00', magenta: '#ff00ff', blue: '#0047bb', none: null };

/** 문서의 그림 레이어들 (아래 → 위) */
function drawableLayers(doc) {
  return doc.layers.filter(l => !l.isFolder && l.kind !== 'adjust' && l.canvas && l.canvas.width > 1);
}

/** 문서 + 부위 설정 → 리그 */
export function rigFromDoc(doc) {
  const cfg = doc.vtuber?.parts || {};
  const s = Math.min(1, MAX_SIDE / Math.max(doc.width, doc.height));
  const parts = [];
  for (const l of drawableLayers(doc)) {
    const c = cfg[l.id];
    if (!c || !c.role || c.role === 'none') continue;
    const cut = V.cutPart(l.canvas, s);
    if (!cut) continue;
    parts.push({ id: l.id, name: l.name, role: c.role, depth: c.depth ?? V.ROLES[c.role]?.depth ?? 0.3, key: c.key || 0, ...cut });
  }
  return V.buildRig(parts, Math.round(doc.width * s), Math.round(doc.height * s), doc.vtuber?.opts || {});
}

// ---------------------------------------------------------------------
// 부위 정하기
// ---------------------------------------------------------------------
export function setupDialog() {
  const doc = app.doc;
  if (!doc) return;
  const layers = drawableLayers(doc).slice().reverse();          // 위 레이어부터 보여 줌
  if (!layers.length) { app.toast('부위로 쓸 그림 레이어가 없습니다'); return; }
  const cfg = structuredClone(doc.vtuber?.parts || {});
  const roleOpts = Object.entries(V.ROLES).map(([k, r]) => [k, r.name]);
  const list = h('div.vt-parts');
  for (const l of layers) {
    const c = cfg[l.id] ||= { role: V.guessRole(l.name) };
    const th = h('canvas', { width: 48, height: 48 });
    const tx = th.getContext('2d'), s = Math.min(48 / l.canvas.width, 48 / l.canvas.height);
    tx.drawImage(l.canvas, (48 - l.canvas.width * s) / 2, (48 - l.canvas.height * s) / 2, l.canvas.width * s, l.canvas.height * s);
    const sel = h('select', { onchange: () => { c.role = sel.value; key.style.visibility = c.role === 'expr' ? 'visible' : 'hidden'; } }, ...roleOpts.map(([k, n]) => h('option', { value: k, selected: c.role === k }, n)));
    const depth = h('input', { type: 'number', min: -1, max: 1.5, step: 0.05, value: c.depth ?? '', placeholder: '자동', title: '깊이 (클수록 고개 방향으로 더 움직임)', oninput: () => { c.depth = depth.value === '' ? undefined : +depth.value; } });
    const key = h('select', { title: '표정 단축키', onchange: () => { c.key = +key.value; } }, ...[1, 2, 3, 4, 5, 6, 7, 8, 9].map(k => h('option', { value: k, selected: (c.key || 1) === k }, `키 ${k}`)));
    if (!c.key) c.key = 1;
    key.style.visibility = c.role === 'expr' ? 'visible' : 'hidden';
    list.append(h('div.vt-part', null, th, h('span.vt-name', { title: l.name }, l.name), sel, depth, key));
  }
  const body = h('div', null,
    h('div.help', null, '레이어마다 어떤 부위인지 고르세요 (이름으로 미리 짐작해 두었습니다). 눈(뜸·감음), 입(닫힘·벌림, 있으면 ㅏㅣㅜㅔㅗ), 앞머리·뒷머리를 따로 그려 두면 깜빡임·입 모양·머리카락 흔들림이 됩니다. PNG 버튜버는 "PNG: 가만히 / 말할 때 / 눈 감음" 그림 몇 장이면 됩니다.'),
    list);
  openModal({
    title: '버튜버 아바타 만들기 (부위 정하기)', body, wide: true,
    actions: [{ label: '취소' }, { label: '저장하고 스튜디오 열기', primary: true, run: () => {
      doc.vtuber = { ...(doc.vtuber || {}), parts: cfg };
      doc.modified = true; app.bus.emit('doc:modified', true);
      setTimeout(() => studio(), 0);
    } }],
  });
}

// ---------------------------------------------------------------------
// 스튜디오 (미리보기·추적·방송 창·녹화)
// ---------------------------------------------------------------------
let live = null;          // 열린 스튜디오 하나만

export async function studio(rigIn = null) {
  if (live) { app.toast('버튜버 스튜디오가 이미 열려 있습니다'); return; }
  const doc = app.doc;
  if (!rigIn && !doc?.vtuber?.parts) { setupDialog(); return; }
  let rig = rigIn || rigFromDoc(doc);
  if (!rig.parts.length) { app.toast('부위가 정해진 레이어가 없습니다. 부위를 먼저 정하세요', 'warn'); setupDialog(); return; }
  const o = rig.opts;
  const S = live = { cam: null, mic: null, lm: null, popup: null, rec: null, expr: 0, bg: o.bg || 'green', state: V.neutral(), ph: V.makePhysics(), raf: 0, lastTs: 0, track: null, seed: Math.random() * 10 };
  // 방송용 원본 크기 캔버스 (녹화·방송 창 공용)
  const stage = createCanvas(Math.round(rig.w / 2) * 2, Math.round(rig.h / 2) * 2), sx = stage.getContext('2d');
  const prevS = Math.min(1, 520 / Math.max(rig.w, rig.h));
  const view = h('canvas.vt-view', { width: Math.round(rig.w * prevS), height: Math.round(rig.h * prevS) });
  const vx = view.getContext('2d');
  const status = h('div.help.vt-status', null, '카메라나 마이크를 켜면 따라 움직입니다. 꺼져 있으면 숨쉬기·깜빡임만.');
  const setStatus = (t) => { status.textContent = t; };
  const exprKeys = [...new Set(rig.parts.filter(p => p.role === 'expr').map(p => p.key))].sort();
  const exprRow = h('div.s3d-prims');
  const syncExpr = () => { exprRow.innerHTML = ''; for (const k of [0, ...exprKeys]) exprRow.append(h(`button.btn${S.expr === k ? '.primary' : ''}`, { type: 'button', onclick: () => { S.expr = k; syncExpr(); } }, k ? `표정 ${k}` : '기본 (0)')); };
  syncExpr();

  const camBtn = button('카메라 얼굴 추적 켜기', async () => {
    if (S.cam) { S.cam.stop(); S.cam = null; camBtn.textContent = '카메라 얼굴 추적 켜기'; setStatus('카메라를 껐습니다'); return; }
    try {
      setStatus('얼굴 추적 준비 중…');
      const T = await import('../vtuber/track.js');
      S.lm ||= await T.faceLandmarker();
      S.cam = await T.startCamera();
      camBtn.textContent = '카메라 끄기';
      setStatus('카메라 추적 중 (영상은 저장·전송하지 않습니다)');
    } catch (e) { setStatus('카메라를 켜지 못했습니다: ' + (e.message || e)); }
  }, 'primary');
  const micBtn = button('마이크로 입 움직이기', async () => {
    if (S.mic) { S.mic.stop(); S.mic = null; micBtn.textContent = '마이크로 입 움직이기'; return; }
    try {
      const T = await import('../vtuber/track.js');
      S.mic = await T.startMic();
      micBtn.textContent = '마이크 끄기';
    } catch (e) { setStatus('마이크를 켜지 못했습니다: ' + (e.message || e)); }
  });
  const meter = h('div.vt-meter', null, h('i'));

  const popBtn = button('방송 창 열기 (OBS 창 캡처용)', () => openPopup());
  const recBtn = button('녹화 시작', () => toggleRec());
  const save = async (name, blob) => { if (!server.online) downloadBlob(blob, name); else await server.put('exports', name, blob); };

  const openPopup = () => {
    if (S.popup && !S.popup.closed) { S.popup.focus(); return; }
    const w = Math.min(1280, stage.width), hh = Math.round(w * stage.height / stage.width);
    const win = window.open('', 'comicpad-vtuber', `popup=yes,width=${w},height=${hh}`);
    if (!win) { app.toast('창이 막혔습니다. 팝업을 허용해 주세요', 'warn'); return; }
    const d = win.document;
    d.title = 'ComicPad 버튜버 (방송용)';
    d.body.style.cssText = 'margin:0;overflow:hidden;background:#000';
    const c = d.createElement('canvas');
    c.width = stage.width; c.height = stage.height;
    c.style.cssText = 'width:100vw;height:100vh;object-fit:contain;display:block';
    d.body.append(c);
    win.addEventListener('keydown', onKey);
    S.popup = win; S.popupCtx = c.getContext('2d');
    S.tick();
    setStatus('방송 창을 열었습니다. OBS 에서 "창 캡처" → 이 창, 색 키(크로마 키)로 배경 색을 빼세요');
  };

  const toggleRec = async () => {
    if (S.rec) { S.rec.stop(); return; }
    const stream = stage.captureStream(30);
    if (S.mic) for (const t of S.mic.stream.getAudioTracks()) stream.addTrack(t);
    const mp4 = ['video/mp4;codecs=avc1', 'video/mp4'].find(t => MediaRecorder.isTypeSupported(t));
    const type = mp4 || (MediaRecorder.isTypeSupported('video/webm;codecs=vp9') ? 'video/webm;codecs=vp9' : 'video/webm');
    const rec = new MediaRecorder(stream, { mimeType: type, videoBitsPerSecond: 8e6 });
    const chunks = [];
    rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
    rec.onstop = async () => {
      S.rec = null; recBtn.textContent = '녹화 시작';
      const blob = new Blob(chunks, { type: mp4 ? 'video/mp4' : 'video/webm' });
      const name = `${safeName(doc?.name || '버튜버')}_버튜버_${fileStamp()}.${mp4 ? 'mp4' : 'webm'}`;
      await save(name, blob);
      app.toast(`녹화 저장: data/exports/${name}`);
    };
    rec.start(1000);
    S.rec = rec; recBtn.textContent = '■ 녹화 멈추고 저장';
  };

  const snap = async () => {
    const name = `${safeName(doc?.name || '버튜버')}_버튜버_${fileStamp()}.png`;
    await save(name, await canvasToBlob(stage));
    app.toast(`사진 저장: data/exports/${name}`);
  };

  const onKey = (e) => {
    const t = e.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
    if (/^[0-9]$/.test(e.key) && !e.ctrlKey && !e.altKey && !e.metaKey) {
      const k = +e.key;
      S.expr = k === 0 || exprKeys.includes(k) ? k : S.expr;
      syncExpr();
      e.preventDefault(); e.stopImmediatePropagation();
    }
  };
  window.addEventListener('keydown', onKey, true);

  const saveFile = async () => {
    const json = await V.rigToFile(rig, doc?.name || '아바타');
    const blob = new Blob([JSON.stringify(json)], { type: 'application/json' });
    const name = `${safeName(json.name)}_${fileStamp()}.cpvt`;
    await save(name, blob);
    app.toast(`아바타 파일 저장: data/exports/${name} (다른 작품 없이 이 파일만 열어 방송할 수 있습니다)`);
  };

  const bgSeg = segmented([['green', '초록'], ['magenta', '자홍'], ['blue', '파랑'], ['none', '투명']], () => S.bg, (v) => { S.bg = v; o.bg = v; });
  const body = h('div.vt-studio', null,
    h('div.vt-left', null, view, status),
    h('div.vt-right', null,
      section('따라 움직이기', row(camBtn, micBtn), meter,
        toggle('거울처럼 (내가 오른쪽을 보면 캐릭터도 화면 오른쪽)', () => o.mirror !== false, v => { o.mirror = v; })),
      section('조절',
        slider('움직임 크기', { min: 0, max: 2, step: 0.05, get: () => o.motion ?? 1, set: v => { o.motion = v; }, format: v => Math.round(v * 100) + '%' }),
        slider('입 민감도', { min: 0.2, max: 4, step: 0.1, get: () => o.mouthGain ?? 1, set: v => { o.mouthGain = v; }, format: v => v.toFixed(1) + '배' }),
        slider('부드럽게 (떨림 줄이기)', { min: 0, max: 0.9, step: 0.05, get: () => o.smooth ?? 0.5, set: v => { o.smooth = v; }, format: v => Math.round(v * 100) + '%' })),
      section('표정 (키보드 1~9, 0 = 기본)', exprKeys.length ? exprRow : h('div.help', null, '부위 정하기에서 "표정" 레이어에 키를 주면 여기서 켤 수 있습니다.')),
      section('방송·녹화', h('div.help', null, '배경 색'), bgSeg, row(popBtn), row(recBtn, button('사진 저장', snap)),
        h('div.help', null, '방송 창은 캐릭터만 보입니다. OBS 의 "창 캡처"로 잡고 색 키로 배경을 빼세요. 녹화는 MP4(되면) 또는 WebM 으로 data/exports 에 저장됩니다.')),
      section('아바타', row(button('부위 다시 정하기', () => { modal.close(); setTimeout(setupDialog, 0); }), button('.cpvt 로 저장', saveFile)))),
  );

  // ---- 움직이기 ----
  let last = performance.now();
  // 다음 장 예약: 방송 창이 열려 있으면 그 창 기준 (본 창을 내려도 방송이 멈추지 않게), 본 창이 가려져 있으면 타이머로
  const schedule = () => {
    if (live !== S) return;
    const w = S.popup && !S.popup.closed ? S.popup : null;
    if (w && !w.document.hidden) S.raf = { win: w, id: w.requestAnimationFrame(() => frame()) };
    else if (document.hidden) S.raf = { timer: setTimeout(() => frame(), 33) };
    else S.raf = { win: window, id: requestAnimationFrame(() => frame()) };
  };
  const unschedule = () => { const r = S.raf; if (!r) return; if (r.timer) clearTimeout(r.timer); else try { r.win.cancelAnimationFrame(r.id); } catch { /* 창 닫힘 */ } S.raf = null; };
  const frame = () => {
    const now = performance.now();
    if (live !== S) return;
    const dt = (now - last) / 1000; last = now;
    let next = { ...V.neutral() };
    let tracked = false;
    if (S.cam && S.lm && S.cam.video.readyState >= 2) {
      const ts = Math.max(S.lastTs + 1, Math.round(now));
      S.lastTs = ts;
      try {
        const r = S.lm.detectForVideo(S.cam.video, ts);
        if (r?.faceBlendshapes?.length) { next = V.faceToState(r.faceBlendshapes[0].categories, r.facialTransformationMatrixes?.[0]?.data, o.mirror !== false); tracked = true; }
      } catch { /* 한 장 실패는 무시 */ }
    }
    if (!tracked) {
      // 추적 없음: 자동 깜빡임 + 아주 작은 흔들림
      const b = V.autoBlink(now / 1000, S.seed);
      next.blinkL = next.blinkR = b;
      next.yaw = Math.sin(now / 2600) * 0.06; next.roll = Math.sin(now / 3300) * 0.04;
    }
    next.mouth *= (o.mouthGain ?? 1);
    if (S.mic) {
      const lv = S.mic.level();
      const m = V.levelToMouth(lv, o.mouthGain ?? 1);
      next.mouth = Math.max(next.mouth, m);
      next.talk = m > 0.15 ? 1 : 0;
      meter.firstChild.style.width = Math.min(100, lv * 600) + '%';
    } else next.talk = next.mouth > 0.2 ? 1 : 0;
    next.mouth = Math.min(1, next.mouth);
    next.t = now / 1000;
    next.expr = S.expr;
    S.state = V.smoothState(S.state, next, o.smooth ?? 0.5);
    S.state.t = next.t; S.state.expr = S.expr;
    const sway = V.stepPhysics(S.ph, S.state, dt);
    V.renderRig(sx, rig, S.state, sway, { bg: BGS[S.bg] });
    vx.clearRect(0, 0, view.width, view.height);
    vx.drawImage(stage, 0, 0, view.width, view.height);
    if (S.popup && !S.popup.closed) {
      const pc = S.popupCtx;
      if (BGS[S.bg]) pc.drawImage(stage, 0, 0); else { pc.clearRect(0, 0, stage.width, stage.height); pc.drawImage(stage, 0, 0); }
    }
    schedule();
  };
  S.tick = () => { unschedule(); frame(); };        // 시험·즉시 다시 그리기용
  schedule();

  const stopAll = () => {
    unschedule();
    window.removeEventListener('keydown', onKey, true);
    S.cam?.stop(); S.mic?.stop();
    if (S.rec) S.rec.stop();
    try { S.lm?.close(); } catch { /* 이미 닫힘 */ }
    if (S.popup && !S.popup.closed) S.popup.close();
    if (doc && !rigIn) { doc.vtuber = { ...(doc.vtuber || {}), opts: { ...o } }; }
    live = null;
  };
  const modal = openModal({ title: '버튜버 스튜디오', body, wide: true, cls: 'vt-modal', onClose: stopAll, actions: [{ label: '닫기', primary: true }] });
  S.rigRef = () => rig;
  return S;
}

/** 지금 열린 스튜디오 (자동 점검용) */
export const currentStudio = () => live;

/** .cpvt 아바타 파일 열기 → 스튜디오 */
export async function openAvatarFile() {
  const f = await pickFile('.cpvt,application/json');
  if (!f) return;
  try { studio(await V.fileToRig(JSON.parse(await f.text()))); } catch (e) { app.toast(e.message, 'error'); }
}

export function initVtuber() {
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('vt.studio', '버튜버 스튜디오 (웹캠·마이크로 캐릭터 움직이기)…', () => studio());
  C('vt.setup', '버튜버 아바타 만들기 (레이어를 부위로)…', setupDialog);
  C('vt.open', '버튜버 아바타 파일 열기 (.cpvt)…', openAvatarFile);
  for (const it of [{ sep: true }, { command: 'vt.studio' }, { command: 'vt.setup' }, { command: 'vt.open' }]) app.addMenuItem('ai', it);
}
