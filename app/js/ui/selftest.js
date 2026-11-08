// 자동 점검: 주요 기능을 시험용 그림에서 한 번씩 돌려 보고 결과를 표로 보여 준다.
// 지금 작업 중인 그림과 실행취소 기록은 건드리지 않고, 끝나면 그대로 되돌린다.
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';
import { Doc } from '../core/doc.js';
import * as ops from '../core/ops.js';

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const count = (layer) => { const d = layer.ctx.getImageData(0, 0, layer.canvas.width, layer.canvas.height).data; let n = 0; for (let i = 3; i < d.length; i += 4) if (d[i]) n++; return n; };
const assert = (ok, msg) => { if (!ok) throw new Error(msg); };

function scratchDoc() {
  const doc = new Doc(800, 600, { name: '__자체점검' });
  const l = doc.createLayer({ name: '레이어 1' });
  doc.layers.push(l);
  doc.activeId = l.id;
  return doc;
}

const ev = (x, y, p = 0.6, t = 0) => { const s = app.view.toScreen(x, y); return { x, y, sx: s.x, sy: s.y, pressure: p, type: 'pen', buttons: 1, time: t, tiltX: 0, tiltY: 0 }; };
async function stroke(tool, pts) {
  tool.down(ev(...pts[0], 0.6, 0));
  pts.slice(1).forEach((q, i) => tool.move(ev(q[0], q[1], 0.6, (i + 1) * 16)));
  tool.up(ev(...pts.at(-1), 0.6, pts.length * 16));
  await sleep(10);
}
const line = (x0, y0, x1, y1, n = 24) => Array.from({ length: n + 1 }, (_, i) => [x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n]);

const TESTS = [
  ['레이어 추가·삭제·되돌리기', async () => {
    const n = app.doc.layers.length;
    ops.addLayer({ name: 't' });
    assert(app.doc.layers.length === n + 1, '추가 안 됨');
    app.history.undo();
    assert(app.doc.layers.length === n, '되돌리기 실패');
  }],
  ['브러시로 그리기', async () => {
    app.setTool('brush');
    const B = await import('../tools/brush.js');
    const keep = app.settings.activePreset.brush;
    app.settings.activePreset.brush = B.presets('brush').find(p => !p.roughOnly && p.tip !== 'image').id;
    const L = app.doc.active, before = count(L);
    await stroke(app.tool, line(100, 100, 500, 140));
    app.settings.activePreset.brush = keep;
    assert(count(L) > before + 200, '칠해지지 않음');
    return `${count(L) - before}px`;
  }],
  ['지우개', async () => {
    const L = app.doc.active;
    L.ctx.fillStyle = '#000'; L.ctx.fillRect(100, 80, 400, 120); L.touch();
    app.setTool('eraser');
    const before = count(L);
    await stroke(app.tool, line(300, 60, 300, 180));
    assert(count(L) < before, '지워지지 않음');
  }],
  ['벡터 선: 그리기·선 지우기·다시 그리기', async () => {
    const V = await import('../core/vector.js');
    const L = ops.addLayer({ name: 'v', kind: 'vector', meta: { strokes: [] } });
    const pts = []; for (let i = 0; i <= 20; i++) pts.push(100 + i * 15, 300, 0.6, i * 10);
    V.setStrokes(L, [{ pts, preset: { size: 6, opacity: 1 }, color: '#000' }], 't');
    const a = count(L);
    assert(a > 100, '그려지지 않음');
    V.eraseAt(L, 250, 300, 6, false); V.renderVector(L);
    assert(L.meta.strokes.length === 2, '선이 둘로 나뉘지 않음');
    return `${L.meta.strokes.length}조각`;
  }],
  ['말풍선: 만들기·옮기기·되돌리기', async () => {
    app.setTool('bubble');
    const t = app.tool, auto = t.opt.autoText;
    t.opt.autoText = false;
    t.down(ev(200, 200)); t.move(ev(360, 300)); t.up(ev(360, 300)); t.down(ev(220, 340)); t.up(ev(220, 340));
    t.opt.autoText = auto;
    const bl = app.doc.layers.find(l => l.kind === 'bubble');
    assert(bl?.meta?.bubbles?.length === 1, '말풍선이 안 생김');
    const x0 = bl.meta.bubbles[0].x;
    t.down(ev(280, 250)); t.move(ev(380, 250)); t.up(ev(380, 250));
    assert(bl.meta.bubbles[0].x > x0 + 50, '옮겨지지 않음');
    app.history.undo();
    assert(Math.abs(bl.meta.bubbles[0].x - x0) < 1, '되돌리기 실패');
    t.sel = null;
  }],
  ['글자 (자간 포함)', async () => {
    const C = await import('../tools/comic.js');
    const L = ops.addLayer({ name: 'txt', kind: 'text', meta: { ...C.TEXT_DEFAULTS, text: '가나다', x: 100, y: 400, align: 'left' } });
    C.renderText(L);
    const w0 = L.meta.box.w;
    L.meta.tracking = 50; C.renderText(L);
    assert(L.meta.box.w > w0, '자간이 안 늘어남');
    assert(count(L) > 50, '글자가 안 그려짐');
  }],
  ['루비·문장부호 좁게', async () => {
    const C = await import('../tools/comic.js');
    const L = ops.addLayer({ name: 'ruby', kind: 'text', meta: { ...C.TEXT_DEFAULTS, text: '{漢字|かんじ}だ!?', x: 100, y: 200, align: 'left' } });
    C.renderText(L);
    assert(L.meta.box.y < 200, '루비 자리가 안 잡힘');
    const w0 = L.meta.box.w;
    L.meta.kern = true; C.renderText(L);
    assert(L.meta.box.w < w0, '문장부호가 안 좁아짐');
    L.meta.vertical = true; C.renderText(L);
    assert(count(L) > 50, '세로쓰기 루비 그리기 실패');
  }],
  ['말풍선 이어 붙이기', async () => {
    const C = await import('../tools/comic.js');
    const bl = C.bubbleLayer();
    bl.meta.bubbles = [{ id: 'a', x: 100, y: 100, w: 120, h: 80, shape: 'ellipse', line: 4, fill: '#fff', stroke: '#000', group: 'g1' }, { id: 'b', x: 190, y: 120, w: 120, h: 80, shape: 'ellipse', line: 4, fill: '#fff', stroke: '#000', group: 'g1' }];
    C.renderBubbles(bl);
    // 겹친 곳 안쪽에는 테두리(검정)가 없어야 함
    const d = bl.ctx.getImageData(205, 160, 1, 1).data;
    assert(d[0] > 200 && d[3] > 200, `겹친 곳에 선이 남음 (${d[0]})`);
  }],
  ['벡터 조절점 옮기기', async () => {
    const V = await import('../core/vector.js');
    const L = ops.addLayer({ name: 'vp', kind: 'vector', meta: { strokes: [] } });
    const pts = []; for (let i = 0; i <= 20; i++) pts.push(100 + i * 15, 500, 0.6, i * 10);
    V.setStrokes(L, [{ pts, preset: { size: 4, opacity: 1 }, color: '#000' }], 't');
    app.setTool('vselect');
    const t = app.tool;
    t.sel = new Set([0]);
    const s = L.meta.strokes[0], k = t._handles(s)[5];
    const px = s.pts[k * 4], py = s.pts[k * 4 + 1];
    t.down(ev(px, py)); t.move(ev(px, py - 60)); t.up(ev(px, py - 60));
    await sleep(30);
    const n = L.meta.strokes[0].pts;
    assert(n[k * 4 + 1] < py - 40, '점이 안 움직임');
    assert(Math.abs(n[1] - 500) < 4, `먼 끝점까지 크게 움직임 (${(500 - n[1]).toFixed(1)}px)`);
    t.sel = new Set();
  }],
  ['채우기 영역 계산', async () => {
    const R = await import('../core/raster.js');
    const c = document.createElement('canvas'); c.width = 100; c.height = 100;
    const x = c.getContext('2d'); x.strokeRect(20.5, 20.5, 50, 50);
    const { mask, bounds } = await R.fillMaskAsync(x.getImageData(0, 0, 100, 100), 40, 40, { tolerance: 10, gap: 0, expand: 0 });
    assert(bounds && bounds.w < 52 && bounds.w > 45, `영역 크기 이상: ${bounds?.w}`);
    assert(mask[40 * 100 + 40] && !mask[5 * 100 + 5], '안팎 구분 실패');
  }],
  ['선택 영역 (전체·반전)', async () => {
    app.run('select.all');
    assert(app.doc.selection.active, '전체 선택 안 됨');
    app.run('select.none');
    assert(!app.doc.selection.active, '선택 해제 안 됨');
  }],
  ['여러 레이어 묶기', async () => {
    const a = ops.addLayer({ name: 'a' }), b = ops.addLayer({ name: 'b' });
    app.doc.multi = new Set([a.id, b.id]);
    const f = ops.groupLayers();
    assert(f?.isFolder && app.doc.childrenOf(f).length === 2, '폴더로 안 묶임');
    app.history.undo();
    assert(!app.doc.layers.includes(f), '되돌리기 실패');
  }],
  ['밑색 영역 나누기', async () => {
    const A = await import('../core/autoflat.js');
    const w = 120, hh = 80, d = new Uint8ClampedArray(w * hh * 4);
    for (let y = 10; y < 70; y++) for (const x of [10, 60, 110]) d[(y * w + x) * 4 + 3] = 255;
    for (let x = 10; x <= 110; x++) for (const y of [10, 70]) d[(y * w + x) * 4 + 3] = 255;
    const { count: n } = A.flatLabels(d, w, hh, { gap: 0, under: 1, minArea: 5 });
    assert(n === 2, `영역 ${n}개 (2개여야 함)`);
  }],
  ['저장·다시 열기 (픽셀 그대로)', async () => {
    const json = JSON.parse(JSON.stringify(await app.doc.serialize()));
    const d2 = await Doc.deserialize(json);
    const a = app.doc.layers[0], b = d2.layers[0];
    const pa = a.ctx.getImageData(0, 0, 800, 600).data, pb = b.ctx.getImageData(0, 0, 800, 600).data;
    let diff = 0; for (let i = 0; i < pa.length; i += 97) if (pa[i] !== pb[i]) diff++;
    assert(diff === 0, `픽셀 ${diff}곳 다름`);
    assert(d2.layers.length === app.doc.layers.length, '레이어 수 다름');
    return `${(JSON.stringify(json).length / 1024).toFixed(0)}KB`;
  }],
  ['인쇄용 PDF·1비트 TIFF', async () => {
    const P = await import('./printout.js');
    const pdf = P.buildPDF([{ wPt: 100, hPt: 100, w: 8, h: 8, bpc: 1, cs: 'DeviceGray', filter: 'FlateDecode', data: new Uint8Array([120, 156, 3, 0, 0, 0, 0, 1]) }]);
    const head = new TextDecoder().decode((await pdf.arrayBuffer()).slice(0, 8));
    assert(head.startsWith('%PDF-1.4'), 'PDF 머리글 이상');
    const tif = P.tiff1bit(new Uint8Array(2), 8, 2, 600);
    const v = new DataView(await tif.arrayBuffer());
    assert(v.getUint16(0) === 0x4949 && v.getUint16(2, true) === 42, 'TIFF 머리글 이상');
  }],
  ['용지 질감 합성', async () => {
    app.doc.paper.texture = { type: 'fine', strength: 0.8, scale: 1 };
    const c = app.compositor.flatten(app.doc, {});
    const d = c.getContext('2d').getImageData(700, 500, 60, 60).data;
    let mn = 255; for (let i = 0; i < d.length; i += 4) mn = Math.min(mn, d[i]);
    app.doc.paper.texture = null;
    assert(mn < 255, '결이 안 보임');
  }],
  ['3D 인형·손 계산', async () => {
    const E = await import('../three/engine.js');
    const m = E.mannequinMatrices(E.POSES['걷기'], E.BODY_PRESETS['SD 3등신']);
    const hm = E.handMatrices(E.HAND_POSES['주먹'], -1);
    assert(Object.values(m).every(x => x.every(Number.isFinite)) && Object.values(hm).every(x => x.every(Number.isFinite)), '행렬 계산 오류');
    return `관절 ${Object.keys(m).length} + 손 ${Object.keys(hm).length}`;
  }],
  ['ABR 브러시 읽기', async () => {
    const A = await import('../core/abr.js');
    // 버전 1, 그림 브러시 1개 (4x2, 압축 없음)
    const body = [0, 0, 0, 0, 0, 25, 1, 0, 0, 0, 0, 0, 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 4, 0, 8, 0, 255, 0, 255, 0, 0, 255, 0, 255];
    const buf = new Uint8Array([0, 1, 0, 1, 0, 2, 0, 0, 0, body.length, ...body]);
    const s = A.readABR(buf.buffer);
    assert(s.length === 1 && s[0].w === 4 && s[0].h === 2 && s[0].mask[0] === 255, 'ABR 해석 오류');
  }],
  ['기기 저장소 읽고 쓰기', async () => {
    const S = await import('../core/storage.js');
    await S.deviceStore.put('exports', '__selftest.txt', 'ok');
    const t = await (await S.deviceStore.get('exports', '__selftest.txt')).text();
    await S.deviceStore.del('exports', '__selftest.txt');
    assert(t === 'ok', '읽은 값이 다름');
  }],
  ['브러시 동작 (기울기·색 흔들림)', async () => {
    const Br = await import('../core/brush.js');
    const p = Br.makePreset({ size: 20, tiltSize: 1, hueJitter: 0.5, valJitter: 0.3 });
    const s = new Br.Stroke({ canvas: document.createElement('canvas'), preset: p, color: '#3366cc', doc: app.doc });
    const flat = s._size(1);
    s.tiltMag = 1;
    assert(s._size(1) > flat * 2, '눕혀도 안 굵어짐');
    const cols = new Set(Array.from({ length: 40 }, () => s._dabColor(1)));
    assert(cols.size > 10, `색이 안 흔들림 (${cols.size}가지)`);
    return `굵기 ×${(s._size(1) / flat).toFixed(1)}, 색 ${cols.size}가지`;
  }],
  ['애니 카메라 사이 프레임', async () => {
    const X = await import('../core/animx.js');
    const doc = { anim: { camera: { keys: [{ f: 0, x: 0.5, y: 0.5, z: 1, r: 0 }, { f: 10, x: 0.5, y: 0.5, z: 2, r: 20 }], ease: true } } };
    const m = X.cameraAt(doc, 5), q = X.cameraAt(doc, 2);
    assert(Math.abs(m.z - 1.5) < 1e-6 && Math.abs(m.r - 10) < 1e-6, '가운데 값 이상');
    assert(q.z < 1.2, '부드럽게 시작하지 않음');
  }],
  ['BVH 동작 읽기', async () => {
    const Bv = await import('../three/bvh.js');
    const names = [['Hips', null], ['Spine', 'Hips'], ['Chest', 'Spine'], ['Neck', 'Chest'], ['Head', 'Neck'],
      ['LeftArm', 'Chest'], ['LeftForeArm', 'LeftArm'], ['LeftHand', 'LeftForeArm'], ['RightArm', 'Chest'], ['RightForeArm', 'RightArm'], ['RightHand', 'RightForeArm'],
      ['LeftUpLeg', 'Hips'], ['LeftLeg', 'LeftUpLeg'], ['LeftFoot', 'LeftLeg'], ['RightUpLeg', 'Hips'], ['RightLeg', 'RightUpLeg'], ['RightFoot', 'RightLeg']];
    const off = { Spine: [0, 10, 0], Chest: [0, 10, 0], Neck: [0, 10, 0], Head: [0, 5, 0], LeftArm: [8, 8, 0], LeftForeArm: [12, 0, 0], LeftHand: [12, 0, 0], RightArm: [-8, 8, 0], RightForeArm: [-12, 0, 0], RightHand: [-12, 0, 0],
      LeftUpLeg: [5, 0, 0], LeftLeg: [0, -20, 0], LeftFoot: [0, -20, 0], RightUpLeg: [-5, 0, 0], RightLeg: [0, -20, 0], RightFoot: [0, -20, 0] };
    const write = (n, ind) => {
      const kids = names.filter(x => x[1] === n);
      const ch = n === 'Hips' ? 'CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation' : 'CHANNELS 3 Zrotation Xrotation Yrotation';
      let s = `${ind}${n === 'Hips' ? 'ROOT' : 'JOINT'} ${n}\n${ind}{\n${ind}  OFFSET ${(off[n] || [0, 0, 0]).join(' ')}\n${ind}  ${ch}\n`;
      s += kids.length ? kids.map(k => write(k[0], ind + '  ')).join('') : `${ind}  End Site\n${ind}  {\n${ind}    OFFSET 0 5 0\n${ind}  }\n`;
      return s + `${ind}}\n`;
    };
    const nch = 6 + (names.length - 1) * 3;
    const fr = (z) => { const v = new Array(nch).fill(0); v[1] = 90; v[6 + 4 * 3] = z; return v.join(' '); };   // LeftArm Z 회전
    const text = `HIERARCHY\n${write('Hips', '')}MOTION\nFrames: 3\nFrame Time: 0.0333333\n${fr(0)}\n${fr(-45)}\n${fr(-90)}\n`;
    const r = Bv.bvhPoses(text);
    assert(r.poses.length === 3 && r.matched >= 12, `포즈 ${r.poses.length}개, 관절 ${r.matched}개`);
    assert(Math.round(r.fps) === 30, `fps ${r.fps}`);
    const a = JSON.stringify(r.poses[0]), b = JSON.stringify(r.poses[2]);
    assert(a !== b, '팔을 돌려도 자세가 같음');
    return `관절 ${r.matched}개`;
  }],
  ['색 공간 변환 (RGB↔Lab)', async () => {
    const I = await import('../core/icc.js');
    let worst = 0;
    for (const c of [[255, 0, 0], [0, 128, 255], [30, 200, 90], [250, 250, 250]]) {
      const [L, a, b] = I.rgbToLab(...c.map(v => v / 255)), back = I.labToRgb(L, a, b);
      worst = Math.max(worst, ...back.map((v, i) => Math.abs(v * 255 - c[i])));
    }
    assert(worst <= 2, `되돌린 색이 ${worst.toFixed(1)}만큼 다름`);
  }],
  ['PSD 글자 레이어 (편집 가능한 글자)', async () => {
    const T = await import('../core/psdtext.js');
    const C = await import('../tools/comic.js');
    const b = T.tyshBlock({ ...C.TEXT_DEFAULTS, text: '안녕\n하세요', x: 100, y: 100, size: 40 }, { x: 100, y: 100, w: 80, h: 90 });
    const sig = String.fromCharCode(...b.slice(0, 8));
    assert(sig === '8BIM' + 'TySh', '블록 머리 이상');
    const raw = String.fromCharCode(...b);
    assert(raw.includes('EngineData') && raw.includes('TxLr'), '글자 정보가 없음');
    return `${b.length}바이트`;
  }],
  ['선화 정리 (점 지우기·틈 잇기)', async () => {
    const Lc = await import('../core/lineclean.js');
    const w = 60, hh = 40, img = new ImageData(w, hh);
    const put = (x, y) => { img.data[(y * w + x) * 4 + 3] = 255; };
    for (let x = 5; x < 55; x++) if (x !== 30) for (let y = 19; y <= 21; y++) put(x, y);   // 가운데 1px 끊긴 선
    put(10, 5);                                                                          // 먼지
    Lc.cleanLines(img, { specks: 3, close: 1 });
    assert(img.data[(5 * w + 10) * 4 + 3] === 0, '먼지가 남음');
    assert(img.data[(20 * w + 30) * 4 + 3] > 128, '끊긴 곳이 안 이어짐');
  }],
  ['큰 원고 메모리 절약 (압축·되살리기)', async () => {
    const L = app.doc.active;
    L.ctx.fillStyle = '#123'; L.ctx.fillRect(50, 50, 300, 200); L.touch();
    const before = L.ctx.getImageData(0, 0, 800, 600).data.slice();
    app.doc.activeId = null;
    assert(L.freeze(), '압축 안 됨');
    assert(L._canvas.width === 1 && L.displayCanvas.width === 400, '캔버스를 비우지 않음');
    app.compositor.flatten(app.doc, {});
    assert(L.frozen, '화면 그리기에 되살아남');
    const after = L.ctx.getImageData(0, 0, 800, 600).data;
    assert(!L.frozen, '안 되살아남');
    let diff = 0; for (let i = 0; i < after.length; i += 13) if (after[i] !== before[i]) diff++;
    app.doc.activeId = L.id;
    assert(diff === 0, `픽셀 ${diff}곳 다름`);
  }],
  ['초기 세팅 프리셋 (사본에만 적용)', async () => {
    const S = await import('./setuppresets.js');
    const real = JSON.stringify(app.settings);
    const copy = structuredClone(app.settings);
    const ps = S.applyPresetTo(copy, 'ps');
    assert(ps.next.ui.mode === 'full' && ps.next.toolKeys.vselect === 'a' && ps.next.keys['view.actual'] === 'ctrl+1', '포토샵식 값 이상');
    const csp = S.applyPresetTo(copy, 'csp');
    assert(csp.next.startDoc?.dpi === 600 && csp.next.toolKeys.gradient === 'g', '클립스튜디오식 값 이상');
    assert(JSON.stringify(csp.next.brushes) === JSON.stringify(copy.brushes), '브러시가 바뀜');
    const file = S.buildSetFile(ps.next, ['keys', 'layout']);
    const back = S.mergeSetFile(copy, JSON.parse(JSON.stringify(file)), ['keys', 'layout']);
    assert(back.next.toolKeys.vselect === 'a' && back.next.ui.mode === 'full', '세팅 파일 주고받기 실패');
    assert(JSON.stringify(app.settings) === real, '실제 설정이 바뀜');
    return `포토샵식 ${ps.changed.length}곳, 클립스튜디오식 ${csp.changed.length}곳`;
  }],
  ['AI 라이선스 구분 (비상업 경고 대상)', async () => {
    const R = await import('../ai/runtime.js');
    const list = await R.registry();
    const bad = list.filter(m => !['yes', 'check', 'no'].includes(m.commercial) || !m.license || !m.source);
    assert(!bad.length, `표시가 빠진 모델: ${bad.map(m => m.id).join(', ')}`);
    const nc = list.filter(m => m.commercial === 'no').map(m => m.id);
    assert(nc.includes('depth-b') && nc.includes('seg-rmbg14'), '비상업 모델 표시 누락');
    const L = await import('../ai/license.js');
    assert(L.ncName('AI 깊이', { commercial: 'no' }).includes('비상업'), '결과 이름 표시 안 됨');
    return `모델 ${list.length}개 (비상업 ${nc.length})`;
  }],
  ['사진 포즈 → 인형 (계산)', async () => {
    const P = await import('../ai/pose.js');
    // 차렷 자세에서 왼팔만 옆으로 든 사람 (MediaPipe 월드 좌표: y 아래)
    const L = Array.from({ length: 33 }, () => ({ x: 0, y: 0, z: 0, visibility: 1 }));
    const set = (i, x, y) => { L[i] = { x, y, z: 0, visibility: 1 }; };
    set(23, 0.1, 0); set(24, -0.1, 0); set(11, 0.18, -0.5); set(12, -0.18, -0.5); set(7, 0.07, -0.68); set(8, -0.07, -0.68); set(0, 0, -0.66);
    set(13, 0.45, -0.5); set(15, 0.7, -0.5);                 // 왼팔 수평
    set(14, -0.2, -0.22); set(16, -0.21, 0.02);              // 오른팔 아래
    set(25, 0.1, 0.42); set(27, 0.1, 0.85); set(26, -0.1, 0.42); set(28, -0.1, 0.85);
    const pose = P.poseFromLandmarks(L);
    const E = await import('../three/engine.js');
    const W = E.mannequinMatrices(pose, null), p = (j) => [W[j][12], W[j][13], W[j][14]];
    const arm = p('handL').map((v, i) => v - p('upperArmL')[i]);
    assert(Math.abs(arm[1]) < Math.abs(arm[0]) * 0.35 && arm[0] > 0, `왼팔이 옆으로 안 듦 (${arm.map(v => v.toFixed(2))})`);
    const legR = p('footR')[1] - p('thighR')[1];
    assert(legR < 0, '다리 방향 이상');
  }],
  ['내장 AI 실행 (작은 모델이 있으면)', async () => {
    const R = await import('../ai/runtime.js');
    if (!(await R.modelStatus())['up-animevideo']?.installed) return '모델 없음 (건너뜀)';
    const F = await import('../ai/features.js');
    const c = document.createElement('canvas'); c.width = 24; c.height = 20;
    const x = c.getContext('2d'); x.fillStyle = '#fff'; x.fillRect(0, 0, 24, 20); x.fillStyle = '#c00'; x.fillRect(6, 5, 12, 10);
    const out = await F.upscaleCanvas(await R.modelInfo('up-animevideo'), c, 4);
    const d = out.getContext('2d').getImageData(48, 40, 1, 1).data;
    assert(out.width === 96 && out.height === 80, `크기 ${out.width}×${out.height}`);
    assert(d[0] > 150 && d[1] < 90, `색이 이상함 (${d[0]},${d[1]},${d[2]})`);
    return (await R.session('up-animevideo', 'model.onnx')).ep === 'webgpu' ? '그래픽 가속' : 'CPU';
  }],
  ['필압 곡선·흔들림 보정 (끈·무거운 펜)', async () => {
    const B = await import('../core/brush.js');
    const lut = B.curveLut([[0, 0], [0.25, 0.5], [1, 1]]);
    assert(Math.abs(lut[64] - 0.5) < 0.02 && lut[255] > 0.99, '곡선 값 이상');
    assert(B.pressureCurve(0.5, { pressurePts: [[0, 0], [0.5, 0.1], [1, 1]] }) < 0.15, '브러시별 곡선이 안 쓰임');
    const fake = { width: 400, height: 200, selection: { clipTo() {} } };
    const wob = (mode, stab) => {
      const st = new B.Stroke({ canvas: document.createElement('canvas'), preset: B.makePreset({ size: 4, stabilizer: stab, stabMode: mode }), color: '#000', doc: fake, noClip: true });
      const ys = []; const o = st._push.bind(st); st._push = (q) => { ys.push(q); o(q); };
      let l; for (let i = 0; i <= 120; i++) { l = { x: 20 + i * 3, y: 100 + (i % 2 ? 3 : -3), p: 0.6, t: i * 8 }; i ? st.add(l) : st.begin(l); }
      st.end(l);
      let s = 0; for (let i = 2; i < ys.length; i++) s += Math.abs(ys[i].y - 2 * ys[i - 1].y + ys[i - 2].y);
      return { s: s / ys.length, endX: ys.at(-1).x };
    };
    const raw = wob('euro', 0), lazy = wob('lazy', 8), spring = wob('spring', 8);
    assert(lazy.s < raw.s * 0.5 && spring.s < raw.s * 0.5, `떨림이 안 줄어듦 (${raw.s.toFixed(2)} → ${lazy.s.toFixed(2)}, ${spring.s.toFixed(2)})`);
    assert(Math.abs(lazy.endX - 380) < 2 && Math.abs(spring.endX - 380) < 2, '펜 끝까지 안 이어짐');
    return `떨림 ${raw.s.toFixed(1)} → 끈 ${lazy.s.toFixed(2)} / 무거운 펜 ${spring.s.toFixed(2)}`;
  }],
  ['픽셀 연필 (픽셀 퍼펙트)', async () => {
    const B = await import('../core/brush.js');
    const fake = { width: 64, height: 64, selection: { clipTo() {} } };
    const draw = (pp) => {
      const c = document.createElement('canvas'); c.width = 64; c.height = 64;
      const st = new B.Stroke({ canvas: c, preset: B.makePreset({ tip: 'pixel', size: 1, pixelPerfect: pp, pressureSize: false, stabilizer: 0 }), color: '#000', doc: fake, noClip: true });
      const pts = []; for (let i = 0; i < 20; i++) pts.push({ x: 5.5 + i, y: 5.5 + i }, { x: 6.5 + i, y: 5.5 + i });
      st.begin({ ...pts[0], p: 1, t: 0 }); pts.slice(1).forEach((q, i) => st.add({ ...q, p: 1, t: i * 8 })); st.end({ ...pts.at(-1), p: 1, t: 999 });
      const d = c.getContext('2d').getImageData(0, 0, 64, 64).data; let n = 0, semi = 0;
      for (let i = 3; i < d.length; i += 4) if (d[i]) { n++; if (d[i] < 255) semi++; }
      return { n, semi };
    };
    const a = draw(false), b = draw(true);
    assert(!a.semi && !b.semi, '반투명 픽셀이 생김 (안티앨리어싱)');
    assert(b.n < a.n * 0.7, `ㄴ자 모서리가 안 지워짐 (${a.n} → ${b.n})`);
    return `${a.n}px → ${b.n}px`;
  }],
  ['팔레트 색 수 줄이기·명암 잉크', async () => {
    const P = await import('../core/pixel.js');
    const im = new ImageData(32, 32);
    for (let i = 0; i < 1024; i++) { im.data[i * 4] = i % 256; im.data[i * 4 + 1] = (i * 7) % 256; im.data[i * 4 + 2] = 128; im.data[i * 4 + 3] = 255; }
    const pal = P.medianCut(im.data, 8);
    P.quantizeImage(im, pal, 'fs');
    const set = new Set(); for (let i = 0; i < 1024; i++) set.add(im.data[i * 4] + ',' + im.data[i * 4 + 1] + ',' + im.data[i * 4 + 2]);
    assert(pal.length === 8 && set.size <= 8, `색이 ${set.size}가지`);
    const ramp = [[20, 20, 20], [100, 100, 100], [200, 200, 200], [200, 40, 40], [120, 20, 20]];
    const d = P.shadeStep(100, 100, 100, ramp, -1), l = P.shadeStep(100, 100, 100, ramp, 1), r = P.shadeStep(200, 40, 40, ramp, -1);
    assert(d[0] === 20 && l[0] === 200 && l[1] === 200, '회색 사다리 이상');
    assert(r[0] === 120 && r[1] === 20, '같은 계열(빨강)로 안 감');
    assert(P.parsePaletteFile('GIMP Palette\nName: t\n#\n255   0   0\tred\n  0 255   0\n').length === 2, '.gpl 읽기 실패');
    return `${set.size}색`;
  }],
  ['듀얼 브러시·리본', async () => {
    const B = await import('../core/brush.js');
    const ink = (c) => { const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let n = 0; for (let i = 3; i < d.length; i += 4) if (d[i]) n++; return n; };
    const run = (o) => {
      const c = document.createElement('canvas'); c.width = 300; c.height = 100;
      const st = new B.Stroke({ canvas: c, preset: B.makePreset({ size: 30, hardness: 0.9, pressureSize: false, stabilizer: 0, spacing: 0.08, ...o }), color: '#000', doc: { width: 300, height: 100, selection: { clipTo() {} } }, noClip: true });
      let l; for (let i = 0; i <= 40; i++) { l = { x: 30 + i * 6, y: 50 + (o.ribbon ? Math.sin(i / 5) * 15 : 0), p: 1, t: i * 8 }; i ? st.add(l) : st.begin(l); }
      st.end(l);
      return ink(c);
    };
    const base = run({}), dual = run({ dualTip: 'grain', dualDensity: 6 }), rib = run({ ribbon: true, tip: 'grain', grain: 0.6 });
    assert(dual < base * 0.8 && dual > 0, `듀얼이 가리지 않음 (${base} → ${dual})`);
    assert(rib > 1000, '리본이 안 그려짐');
    return `기본 ${base} → 듀얼 ${dual}px`;
  }],
  ['애니 타임라인 (트랙·연결 칸·핑퐁·되돌리기)', async () => {
    const A = await import('../core/anim.js');
    A.createAnimation(2);
    const t1 = A.animFolder();
    A.selectCell(t1, 0); app.doc.active.ctx.fillStyle = '#f00'; app.doc.active.ctx.fillRect(0, 0, 10, 10);
    A.addTrack();
    A.selectCell(t1, 0); A.addFrame('link');
    const xs = A.xsOf(t1);
    assert(A.frameCount() === 3 && xs[0] === xs[1], '연결된 프레임이 안 생김');
    const px = (f) => app.compositor.flatten(app.doc, { frame: f }).getContext('2d').getImageData(5, 5, 1, 1).data[0];
    assert(px(0) > 200 && px(1) > 200, '연결된 칸에 같은 그림이 안 보임');
    A.addTag({ name: 't', from: 0, to: 2, dir: 'ping' }); app.doc.anim.playTag = 0;
    assert(A.playOrder().join() === '0,1,2,1', `핑퐁 순서 ${A.playOrder()}`);
    app.doc.anim.playTag = null;
    app.history.undo(); app.history.undo();
    assert(A.frameCount() === 2, `되돌리기 뒤 ${A.frameCount()}프레임`);
  }],
  ['스프라이트 시트·Aseprite 읽기', async () => {
    const S = await import('../core/sprite.js');
    // 4x2, 레이어 1개, 프레임 2개 (원본 셀 + 연결 셀), 32비트
    const bytes = [];
    const u8 = (...v) => bytes.push(...v), u16 = (v) => u8(v & 255, v >> 8), u32 = (v) => { u16(v & 65535); u16(v >>> 16); };
    const head = () => { u32(0); u16(0xA5E0); u16(2); u16(4); u16(2); u16(32); u32(1); u16(100); u32(0); u32(0); u8(0, 0, 0, 0); u16(0); u8(1, 1); u16(0); u16(0); u16(16); u16(16); for (let i = 0; i < 84; i++) u8(0); };
    head();
    const frame = (chunks, dur) => { const body = chunks.flat(); u32(16 + body.length); u16(0xF1FA); u16(chunks.length); u16(dur); u8(0, 0); u32(chunks.length); bytes.push(...body); };
    const chunk = (type, data) => { const out = []; const n = data.length + 6; out.push(n & 255, (n >> 8) & 255, 0, 0, type & 255, type >> 8, ...data); return out; };
    const name = [...new TextEncoder().encode('L')];
    const layer = chunk(0x2004, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, name.length, 0, ...name]);
    const px = []; for (let i = 0; i < 8; i++) px.push(0, 128, 255, 255);
    const cel = chunk(0x2005, [0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 2, 0, ...px]);
    const link = chunk(0x2005, [0, 0, 0, 0, 0, 0, 255, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    frame([layer, cel], 100); frame([link], 250);
    const d = await S.asepriteToDoc(new Uint8Array(bytes).buffer, 't');
    const A = await import('../core/anim.js');
    const t = A.tracks(d)[0], xs = A.xsOf(t, d);
    assert(A.frameCount(d) === 2 && xs[0] === xs[1], 'Aseprite 연결 셀을 못 읽음');
    assert(d.anim.ms?.[1] === 250, '프레임 시간을 못 읽음');
    const sheet = S.buildSheet(d, { layout: 'h', json: 'hash' });
    assert(sheet.canvas.width === 8 && Object.keys(sheet.json.frames).length === 2, `시트 크기 ${sheet.canvas.width}`);
    const back = S.sliceSheet(sheet.canvas, { json: sheet.json });
    const p = back.frames[1].canvas.getContext('2d').getImageData(1, 1, 1, 1).data;
    assert(back.frames.length === 2 && p[1] === 128 && p[2] === 255, '시트 다시 자르기 실패');
    // .aseprite 로 저장했다가 다시 읽기
    const again = await S.readAseprite(await (await S.docToAseprite(d)).arrayBuffer());
    assert(again.frames.length === 2 && again.frames[1].cels[0]?.type === 1 && again.frames[1].dur === 250, 'Aseprite 저장 왕복 실패');
  }],
  ['복구 브러시 색 맞추기', async () => {
    const Hm = await import('../tools/heal.js');
    const W = 32, n = W * W, src = new Uint8ClampedArray(n * 4), dst = new Uint8ClampedArray(n * 4);
    for (let i = 0; i < n; i++) { const t = (i % W) % 4 < 2 ? 20 : -20; src.fill(100 + t, i * 4, i * 4 + 3); src[i * 4 + 3] = 255; dst.set([200, 60, 60, 255], i * 4); }
    const out = Hm.healPatch(src, dst, W, W, 5);
    let mr = 0, tex = 0; for (let y = 8; y < 24; y++) for (let x = 8; x < 24; x++) mr += out[(y * W + x) * 4];
    mr /= 256;
    for (let y = 8; y < 24; y++) for (let x = 8; x < 24; x++) tex += Math.abs(out[(y * W + x) * 4] - mr);
    assert(Math.abs(mr - 200) < 8, `색이 안 맞음 (${mr.toFixed(0)})`);
    assert(tex / 256 > 10, '결(무늬)이 사라짐');
  }],
  ['내용 인식 채우기 (구멍을 주변 무늬로)', async () => {
    const I = await import('../core/inpaint.js');
    const w = 96, hh = 72, data = new Uint8ClampedArray(w * hh * 4), hole = new Uint8Array(w * hh);
    for (let y = 0; y < hh; y++) for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4, red = Math.floor(y / 8) % 2 === 0;
      data.set(red ? [220, 40, 30, 255] : [30, 40, 210, 255], i);
      if (x >= 36 && x < 60 && y >= 24 && y < 48) { hole[y * w + x] = 1; data.set([0, 0, 0, 255], i); }
    }
    const f = await I.inpaint(data, hole, w, hh);
    let ok = 0, tot = 0;
    for (let y = 24; y < 48; y++) for (let x = 36; x < 60; x++) { if (y % 8 === 0 || y % 8 === 7) continue; const i = (y * w + x) * 4, red = Math.floor(y / 8) % 2 === 0; tot++; if (red ? f[i] > 150 && f[i + 2] < 100 : f[i + 2] > 150 && f[i] < 100) ok++; }
    assert(ok / tot > 0.85, `줄무늬가 안 이어짐 (${Math.round(ok / tot * 100)}%)`);
    return `${Math.round(ok / tot * 100)}% 맞음`;
  }],
  ['펜 패스 → 선택 영역 · 선택 → 패스', async () => {
    const P = await import('../tools/pathpen.js');
    const p = { id: 't', name: 't', closed: true, pts: [[50, 50], [150, 50], [150, 150], [50, 150]].map(([x, y]) => ({ x, y, ix: x, iy: y, ox: x, oy: y })) };
    P.pathToSelection(p);
    const area = () => { const m = app.doc.selection.mask.getContext('2d').getImageData(0, 0, 800, 600).data; let n = 0; for (let i = 3; i < m.length; i += 4) if (m[i] > 127) n++; return n; };
    const a = area();
    assert(Math.abs(a - 10000) < 200, `넓이 ${a}`);
    app.doc.selection.ellipse(300, 200, 200, 160, 'replace');
    const ref = area(), q = P.selectionToPath(1.5);
    P.pathToSelection(q);
    assert(Math.abs(area() / ref - 1) < 0.03, '선택 → 패스 → 선택 모양이 달라짐');
    return `원 → 점 ${q.pts.length}개`;
  }],
  ['자동 액션 기록·재생', async () => {
    const D = await import('./dialogs.js'), M = await import('./macros.js');
    const got = [];
    app.registerCommand('test.macroFx', { label: '시험', run: async () => { const v = await D.form('시험 값', [{ key: 'n', label: 'n', type: 'number', value: 1 }]); if (v) got.push(v.n); } });
    try {
      await M.playMacro({ name: 't', steps: [{ cmd: 'test.macroFx', args: [], forms: [{ title: '시험 값', values: { n: 5 } }] }, { cmd: 'select.all', args: [], forms: [] }] });
      assert(got[0] === 5, '대화상자 값이 안 들어감');
      assert(app.doc.selection.active, '두 번째 명령이 안 됨');
      assert(!document.querySelector('.modal-backdrop .modal'), '대화상자가 떠 있음');
    } finally { app.commands.delete('test.macroFx'); }
  }],
  ['전자책 EPUB 구조 (mimetype 맨 앞, 압축 없음)', async () => {
    const E = await import('./ebook.js'), Z = await import('../core/zip.js');
    const c = document.createElement('canvas'); c.width = 20; c.height = 30;
    const pg = { blob: await new Promise(r => c.toBlob(r, 'image/png')), w: 20, h: 30, type: 'image/png' };
    const z = Z.listZip(new Uint8Array(await (await E.buildEPUB([pg, pg], { title: 't', rtl: true })).arrayBuffer()));
    assert(z[0].name === 'mimetype' && z[0].method === 0 && new TextDecoder().decode(z[0].data) === 'application/epub+zip', 'mimetype 이 맨 앞·무압축이 아님');
    assert(z.some(e => e.name === 'META-INF/container.xml') && z.some(e => e.name === 'OEBPS/content.opf') && z.filter(e => e.name.endsWith('.xhtml')).length === 3, '파일 구성 이상');
    return `파일 ${z.length}개`;
  }],
  ['사진에서 소실점 찾기 (2점 투시)', async () => {
    const V = await import('../ai/vpdetect.js');
    const W = 1000, H = 700, c = document.createElement('canvas'); c.width = W; c.height = H;
    const x = c.getContext('2d'); x.fillStyle = '#f4f0e8'; x.fillRect(0, 0, W, H); x.strokeStyle = '#222'; x.lineWidth = 3;
    const VL = [-300, 260], VR = [1300, 260];
    const to = (p, v, t) => [p[0] + (v[0] - p[0]) * t, p[1] + (v[1] - p[1]) * t];
    const ln = (a, b) => { x.beginPath(); x.moveTo(...a); x.lineTo(...b); x.stroke(); };
    for (const [px, top, bot] of [[500, 80, 620], [380, 150, 520], [640, 140, 540], [250, 200, 450], [800, 190, 470]]) {
      ln([px, top], [px, bot]);
      for (const v of [VL, VR]) for (const y of [top, bot, (top + bot) / 2]) ln([px, y], to([px, y], v, 0.33));
    }
    const vps = V.findVanishingPoints(V.detectSegments(x.getImageData(0, 0, W, H)), W, H, 3);
    const near = (t) => vps.some(v => Math.hypot(v.x - t[0], v.y - t[1]) < 150);
    assert(vps.length === 2 && near(VL) && near(VR), `찾은 점: ${vps.map(v => `${Math.round(v.x)},${Math.round(v.y)}`).join(' / ')}`);
  }],
  ['버튜버 리그 (부위 짐작·고개·깜빡임·입·파일)', async () => {
    const V = await import('../vtuber/rig.js');
    const g = (n) => V.guessRole(n);
    assert(g('왼쪽 눈 감음') === 'eyesClosed' && g('eye_open') === 'eyesOpen' && g('눈썹') === 'brows' && g('입 벌림') === 'mouthOpen' && g('앞머리') === 'hairFront' && g('얼굴') === 'head' && g('mouth_a') === 'mouthA', '부위 짐작 실패');
    const part = (role, color, x, y, w, hh) => { const c = document.createElement('canvas'); c.width = w; c.height = hh; const x2 = c.getContext('2d'); x2.fillStyle = color; x2.fillRect(0, 0, w, hh); return { name: role, role, img: c, x, y, w, h: hh }; };
    const rig = V.buildRig([part('body', '#888', 60, 150, 80, 50), part('head', '#fc9', 50, 40, 100, 110), part('eyesOpen', '#00f', 75, 80, 50, 10), part('eyesClosed', '#f00', 75, 84, 50, 3), part('mouthClosed', '#300', 90, 120, 20, 3), part('mouthOpen', '#0a0', 90, 115, 20, 15)], 200, 200);
    const c = document.createElement('canvas'); c.width = 200; c.height = 200; const ctx = c.getContext('2d');
    const at = (x, y) => [...ctx.getImageData(x, y, 1, 1).data];
    const st = V.neutral();
    V.renderRig(ctx, rig, st, 0, {});
    assert(at(100, 85)[2] > 200, '뜬 눈이 안 보임');
    V.renderRig(ctx, rig, { ...st, blinkL: 1, blinkR: 1 }, 0, {});
    assert(at(100, 85)[0] > 200 && at(100, 82)[2] < 200, '눈 감음으로 안 바뀜');
    V.renderRig(ctx, rig, { ...st, mouth: 0.8 }, 0, {});
    assert(at(100, 125)[1] > 120, '입이 안 벌어짐');
    // 고개를 오른쪽으로 돌리면 얼굴 왼쪽 끝이 오른쪽으로 옮겨 감 (몸은 그대로)
    V.renderRig(ctx, rig, { ...st, yaw: 1 }, 0, {});
    assert(at(52, 60)[3] === 0 && at(70, 170)[0] > 100, '고개 돌림 시차 이상');
    // 행렬 → 각도 (y축 20° 회전)
    const a = 20 * Math.PI / 180, m = [Math.cos(a), 0, -Math.sin(a), 0, 0, 1, 0, 0, Math.sin(a), 0, Math.cos(a), 0, 0, 0, 0, 1];
    assert(Math.abs(Math.abs(V.headAngles(m).yaw) - a) < 1e-6, '고개 각도 계산 이상');
    // 마이크: 조용함 → 0, 큰 소리 → 입 벌림
    const sine = (amp) => Float32Array.from({ length: 1024 }, (_, i) => Math.sin(i / 8) * amp);
    assert(V.levelToMouth(V.rms(sine(0.005))) === 0 && V.levelToMouth(V.rms(sine(0.3))) > 0.8, '마이크 → 입 이상');
    // .cpvt 왕복
    const back = await V.fileToRig(JSON.parse(JSON.stringify(await V.rigToFile(rig, 't'))));
    assert(back.parts.length === rig.parts.length && back.parts[1].role === 'head', '아바타 파일 왕복 실패');
    return `부위 ${rig.parts.length}개`;
  }],
  ['버튜버 PNG 방식 (말할 때 그림 바꾸기)', async () => {
    const V = await import('../vtuber/rig.js');
    const img = (color) => { const c = document.createElement('canvas'); c.width = 40; c.height = 40; const x = c.getContext('2d'); x.fillStyle = color; x.fillRect(0, 0, 40, 40); return c; };
    const rig = V.buildRig([{ role: 'pngIdle', img: img('#00f'), x: 0, y: 0, w: 40, h: 40 }, { role: 'pngTalk', img: img('#f00'), x: 0, y: 0, w: 40, h: 40 }], 40, 60);
    const c = document.createElement('canvas'); c.width = 40; c.height = 60; const ctx = c.getContext('2d');
    V.renderRig(ctx, rig, V.neutral(), 0, {});
    const idle = ctx.getImageData(20, 30, 1, 1).data;
    V.renderRig(ctx, rig, { ...V.neutral(), talk: 1 }, 0, {});
    const talk = ctx.getImageData(20, 30, 1, 1).data;
    assert(rig.png && idle[2] > 200 && talk[0] > 200, '말할 때 그림이 안 바뀜');
  }],
  ['효과음 글자 (휘기·글자별·2중 외곽선)', async () => {
    const S = await import('../tools/sfx.js');
    const C = await import('../tools/comic.js');
    const m = { ...S.SFX_DEFAULTS, text: '두근두근', x: 0, y: 0, chaos: 0, warpAmt: 0.8 };
    const ys = (warp) => S.sfxLayout({ ...m, warp }, (g, s) => s).map(g => g.y);
    assert(ys('none').every(y => y === 0) && ys('arc')[1] < -10 && ys('bounce')[0] !== ys('bounce')[1], '휘기가 안 됨');
    const L = S.createSfxAt(400, 300, '쾅!');
    L.meta.out = { w: 5, color: '#ff0000' }; C.renderText(L);
    let red = 0; const d = L.ctx.getImageData(0, 0, 800, 600).data;
    for (let i = 0; i < d.length; i += 4) if (d[i] > 200 && d[i + 1] < 60 && d[i + 3] > 200) red++;
    assert(L.kind === 'text' && L.meta.sfx && red > 100, '2중 외곽선이 안 그려짐');
    const h0 = L.meta.box.h; L.meta.chars = [{ scale: 2 }]; C.renderText(L);
    assert(L.meta.box.h > h0, '글자별 크기가 안 바뀜');
  }],
  ['컷 나누기 템플릿 (간격·사선·저장)', async () => {
    const T = await import('./paneltemplates.js');
    const area = { x: 100, y: 100, w: 600, h: 400 };
    const p = T.layoutPanels(T.PANEL_TEMPLATES.find(t => t.name === '2단'), area, 20, 30);
    assert(p.length === 2 && p[0].pts[2].y === 285 && p[1].pts[0].y === 315 && p[0].pts[0].x === 100, `간격 이상 ${JSON.stringify(p[0].pts)}`);
    const dg = T.layoutPanels(T.PANEL_TEMPLATES.find(t => t.name === '대각선 2컷'), area, 20, 30);
    assert(dg[0].pts[2].y < dg[1].pts[1].y, '사선 컷 사이 간격 없음');
    const placed = T.applyTemplate(T.PANEL_TEMPLATES.find(t => t.name === '2×2'), { area, gx: 10, gy: 10 });
    const again = T.applyTemplate(T.templateFromCurrent('t', 'margin'), { mode: 'margin', margin: Math.round(app.doc.width * 0.07) });
    assert(placed.length === 4 && again.length === 4, '템플릿 저장·다시 적용 실패');
  }],
  ['인쇄 전 점검 (안전선·회색·러프·도련)', async () => {
    const P = await import('./preflight.js');
    const C = await import('../tools/comic.js');
    const doc = app.doc;
    doc.dpi = 600; doc.print = { trimW: 700, trimH: 500, bleed: 30, safe: 40 };
    const L = doc.active;
    L.ctx.fillStyle = '#000'; L.ctx.fillRect(50, 50, 700, 500); L.ctx.fillStyle = '#888'; L.ctx.fillRect(200, 200, 200, 150); L.touch();
    const t = ops.addLayer({ name: 't', kind: 'text', meta: { ...C.TEXT_DEFAULTS, text: '바깥', x: 5, y: 5, align: 'left' } }); C.renderText(t);
    ops.addLayer({ name: '러프', rough: true });
    const msgs = P.checkDoc(doc).map(x => x.msg).join('|');
    for (const k of ['안전선', '회색', '러프', '도련']) assert(msgs.includes(k), `"${k}" 문제를 못 찾음`);
  }],
  ['모션 경로 (이징·굳히기 결과 같음)', async () => {
    const A = await import('../core/anim.js'), M = await import('../core/motion.js');
    A.createAnimation(1);
    const t = A.animFolder(), c = A.celAt(t, 0);
    c.ctx.fillStyle = '#f00'; c.ctx.fillRect(10, 10, 30, 30);
    A.selectCell(t, 0); for (let i = 0; i < 4; i++) A.addFrame('link');
    M.setKey(t, 0, { x: 0, ease: 'linear' }); M.setKey(t, 4, { x: 200, r: 30, s: 1.5 });
    assert(Math.abs(M.valuesAt(t, 2).x - 100) < 0.5, `중간 값 ${M.valuesAt(t, 2).x}`);
    const img = (f) => app.compositor.flatten(app.doc, { frame: f, background: '#fff' }).getContext('2d').getImageData(0, 0, 400, 300).data;
    const before = img(3);
    A.bakeMotion(t);
    const after = img(3);
    let diff = 0; for (let i = 0; i < before.length; i += 4) if (Math.abs(before[i] - after[i]) + Math.abs(before[i + 1] - after[i + 1]) > 30) diff++;
    assert(diff < 20 && !M.motionOf(t), `굳힌 그림이 다름 (${diff}px)`);
    app.history.undo();
    assert(!!M.motionOf(t), '굳히기 되돌리기 실패');
  }],
  ['립싱크 (모양 고르기·깜빡임 줄이기)', async () => {
    const L = await import('../core/lipsync.js');
    const f = (vol, low, mid, high) => ({ vol, low, mid, high });
    const s = L.classify([f(0, 1, 1, 1), f(0.2, 1, 1, 1), f(0.9, 10, 1, 0.1), f(0.9, 1, 3, 6), f(0.9, 3, 4, 1)]);
    assert(s.join() === 'closed,half,u,i,a', `모양 ${s}`);
    assert(L.smooth(['a', 'a', 'i', 'a', 'a', 'u', 'u']).join() === 'a,a,a,a,a,u,u', '한 장짜리 모양이 남음');
    assert(L.resolve({ closed: 'c', a: 'x' }, 'i') === 'x', '없는 모양 대신하기 실패');
  }],
  ['움짤 형식 (GIF 투명·APNG·WebP)', async () => {
    const A = await import('../core/anim.js'), AX = await import('../core/animexport.js');
    app.doc.width = 40; app.doc.height = 30;
    A.createAnimation(2);
    const t = A.animFolder();
    app.doc.childrenOf(t).forEach((c, i) => { c.ctx.fillStyle = i ? '#00f' : '#f00'; c.ctx.fillRect(5, 5, 10, 10); });
    const o = { scale: 1, loop: 0, transparent: true };
    const bytes = async (b) => new Uint8Array(await b.arrayBuffer());
    const gif = await bytes(await A.encodeGIF({ ...o, colors: 16 }));
    assert(String.fromCharCode(...gif.slice(0, 6)) === 'GIF89a' && gif.includes(0x09), 'GIF 머리·투명 표시 이상');
    const png = await bytes(await AX.encodeAPNG(o)), ps = String.fromCharCode(...png);
    assert(ps.includes('acTL') && ps.includes('fdAT') && (ps.match(/fcTL/g) || []).length === 2, 'APNG 구조 이상');
    const wp = await bytes(await AX.encodeWebP(o)), ws = String.fromCharCode(...wp.slice(0, 64));
    assert(ws.startsWith('RIFF') && ws.includes('WEBPVP8X') && ws.includes('ANIM') && (String.fromCharCode(...wp).match(/ANMF/g) || []).length === 2, 'WebP 구조 이상');
    return `GIF ${gif.length}B · APNG ${png.length}B · WebP ${wp.length}B`;
  }],
  ['자동 백업·빠른 AI 고르기 준비', async () => {
    const S = await import('../core/io.js');
    if (!S.server.http) return '서버 없음 (건너뜀)';
    const st = await (await fetch('/api/backup')).json();
    assert(st.conf && typeof st.conf.keep === 'number' && Array.isArray(st.list), '백업 상태를 못 읽음');
    const R = await import('../ai/runtime.js');
    const sel = (await R.registry()).filter(m => m.task === 'select');
    assert(sel[0]?.id === 'sam-mobile', '탭해서 고르기 기본 모델이 MobileSAM 이 아님');
    return `백업 ${st.list.length}개, 자동 ${st.conf.on ? '켜짐' : '꺼짐'}`;
  }],
  ['색 점으로 칠하기 (영역·이웃 따라가기)', async () => {
    const A = await import('../core/autoflat.js');
    const w = 120, hh = 80, line = new Uint8ClampedArray(w * hh * 4);
    const put = (x, y) => { line[(y * w + x) * 4 + 3] = 255; };
    for (let x = 0; x < w; x++) { put(x, 0); put(x, hh - 1); put(x, 40); }
    for (let y = 0; y < hh; y++) { put(0, y); put(w - 1, y); put(60, y); }          // 영역 4개
    const r = A.flatLabels(line, w, hh, { gap: 0, under: 1, minArea: 5, outside: false });
    const hint = new Uint8ClampedArray(w * hh * 4);
    const dot = (x, y, c) => { const p = (y * w + x) * 4; hint[p] = c[0]; hint[p + 1] = c[1]; hint[p + 2] = c[2]; hint[p + 3] = 255; };
    dot(20, 20, [200, 0, 0]); dot(90, 20, [0, 0, 200]); dot(90, 60, [0, 150, 0]);      // 왼아래는 점 없음
    const out = A.colorizeHints(r.labels, r.count, r.edge, hint, w, hh, { spread: true });
    const at = (x, y) => [...out.img.data.slice((y * w + x) * 4, (y * w + x) * 4 + 3)].join();
    assert(at(20, 20) === '200,0,0' && at(90, 20) === '0,0,200' && at(90, 60) === '0,150,0', '점 색으로 안 칠해짐');
    assert(at(20, 60) !== '0,0,0', '점 없는 영역이 이웃 색을 안 따라감');
    return `영역 ${r.count}개 중 ${out.filled}개 칠함`;
  }],
  ['실행취소·다시실행 일관성', async () => {
    const L = app.doc.active, a = count(L);
    app.history.pixels(L, 't', (c) => { c.fillStyle = '#f00'; c.fillRect(600, 400, 80, 80); });
    const b = count(L);
    app.history.undo(); const c1 = count(L);
    app.history.redo(); const d1 = count(L);
    assert(c1 === a && d1 === b, `되돌리기 ${a}→${c1}, 다시 ${b}→${d1}`);
  }],
];

/** only: 이름에 이 글자가 들어간 항목만 (정규식 가능) */
export const SELFTESTS = TESTS;   // 하나씩 돌려 보기용

export async function runSelfTest(only = null) {
  const saved = { doc: app.doc, undo: app.history.undoStack, redo: app.history.redoStack, bytes: app.history.bytes, tool: app.toolId, multi: app.doc?.multi };
  const results = [];
  app.selftesting = true;
  app.history.undoStack = []; app.history.redoStack = []; app.history.bytes = 0;
  app.doc = scratchDoc();
  app.bus.emit('doc:changed', app.doc);
  try {
    await app.task('자동 점검', async () => {
      for (const [name, fn] of TESTS) {
        if (only && !new RegExp(only).test(name)) continue;
        app.selftestNow = name;                 // 멈췄을 때 어느 항목인지 알 수 있게
        // 항목마다 새 시험용 그림 (앞 항목이 남긴 것과 섞이지 않게)
        app.doc = scratchDoc();
        app.history.undoStack = []; app.history.redoStack = []; app.history.bytes = 0;
        app.bus.emit('doc:changed', app.doc);
        const t0 = performance.now();
        try { const info = await fn(); results.push({ name, ok: true, info: info || '', ms: performance.now() - t0 }); }
        catch (e) { results.push({ name, ok: false, info: e.message || String(e), ms: performance.now() - t0 }); }
        app.doc.multi = null;
      }
    }, { delay: 0 });
  } finally {
    app.doc = saved.doc;
    if (app.doc) app.doc.multi = saved.multi;
    app.history.undoStack = saved.undo; app.history.redoStack = saved.redo; app.history.bytes = saved.bytes;
    app.selftesting = false;
    app.bus.emit('doc:changed', app.doc);
    app.bus.emit('history:changed', app.history);
    app.bus.emit('layers:changed');
    if (saved.tool) app.setTool(saved.tool);
    app.requestRender();
  }
  const bad = results.filter(r => !r.ok);
  const table = h('table.progress-table', null, h('tr', null, h('th', null, ''), h('th', null, '항목'), h('th', null, '결과'), h('th', null, '시간')),
    ...results.map(r => h('tr', null, h('td', null, r.ok ? '✅' : '❌'), h('td', null, r.name), h('td', null, r.info), h('td', null, `${Math.round(r.ms)}ms`))));
  const text = results.map(r => `${r.ok ? 'OK ' : 'FAIL'} ${r.name} ${r.info}`).join('\n');
  openModal({
    title: bad.length ? `자동 점검: ${bad.length}개 문제` : `자동 점검: ${results.length}개 모두 통과`,
    body: h('div', null, h('div.help', null, '시험용 그림에서 돌렸고, 지금 작업 중인 그림과 실행취소 기록은 그대로입니다.'), table),
    wide: true,
    actions: [{ label: '결과 복사', run: () => navigator.clipboard?.writeText(text).then(() => app.toast('복사했습니다')) }, { label: '닫기', primary: true }],
  });
  return results;
}

export function initSelfTest() {
  app.registerCommand('selftest.run', { label: '자동 점검 (주요 기능 시험)', run: runSelfTest });
  app.addMenuItem('help', { command: 'selftest.run' });
}
