// AI 메뉴 + AI 모델 관리 창 (받기·지우기·라이선스 표시)
import { app } from '../app.js';
import { h, pickFile } from '../core/util.js';
import { openModal, confirmDialog } from './dialogs.js';
import { server } from '../core/io.js';
import { registry, modelStatus, closeAll, resetWorker } from '../ai/runtime.js';
import { BADGE, wrapExports, syncNcBadge } from '../ai/license.js';
import { aiLineart, aiCutout, aiDepth, aiUpscale } from '../ai/features.js';
import { aiSelectTool, startAiSelect } from '../ai/aiselect.js';
import { initVP } from '../ai/vpdetect.js';

const TASKS = { pose: '사진 → 인형 자세 (3D 소재 창)', hand: '사진 → 손 모양 (3D 소재 창)', face: '버튜버 얼굴 추적 (웹캠)', select: '탭해서 고르기', cutout: '캐릭터·배경 따기', lineart: '선화 추출', depth: '깊이 추정', upscale: '크기 키우기' };
const mb = (b) => (b ? `${(b / 1e6).toFixed(b < 1e7 ? 1 : 0)}MB` : '');

export async function modelsDialog() {
  if (!server.http) { app.toast('AI 모델은 ComicPad 서버(바탕화면 아이콘)로 켰을 때만 받고 쓸 수 있습니다', 'warn'); return; }
  const list = await registry();
  const body = h('div.aim');
  let timer = null;
  const fetchOne = async (id) => {
    const r = await fetch(`/api/models/fetch?id=${encodeURIComponent(id)}`, { method: 'POST' });
    if (!r.ok) app.toast((await r.json()).error || '받기 실패', 'error');
    render();
  };
  const render = async () => {
    const st = await modelStatus();
    const busy = Object.values(st).some(s => s?.job && !['done', 'error'].includes(s.job.state));
    body.innerHTML = '';
    body.append(h('div.help', null, '받은 모델은 설치 폴더의 data/models 에 있고, 그림은 이 PC 밖으로 나가지 않습니다. ',
      h('b.aim-badge.yes', null, BADGE.yes), ' 마음대로 · ', h('b.aim-badge.check', null, BADGE.check), ' 저장소는 상업 허용이나 모델 파일 표기 없음 · ',
      h('b.aim-badge.no', null, BADGE.no), ' 개인 공부·취미만 (쓸 때 경고, 작품에 기록)'));
    body.append(h('div.row', null,
      h('button.btn', { type: 'button', onclick: async () => { for (const m of list) if (m.commercial === 'yes' && !m.manual && !st[m.id]?.installed) await fetchOne(m.id); } }, '상업 가능한 것 모두 받기'),
      h('button.btn', { type: 'button', onclick: async () => { for (const m of list) if (!m.manual && !st[m.id]?.installed) await fetchOne(m.id); } }, '모두 받기'),
      h('label.cz-row', null, Object.assign(h('input', { type: 'checkbox' }), { checked: !!app.settings.aiCpuOnly, onchange: (e) => { app.settings.aiCpuOnly = e.target.checked; app.saveSettingsSoon(); resetWorker(); app.toast(e.target.checked ? 'AI 를 CPU 로만 돌립니다 (느리지만 그래픽 드라이버 문제를 피함)' : 'AI 에 그래픽 가속을 씁니다'); } }), h('span', null, 'AI 는 CPU 로만 (그래픽 가속 끄기)'))));
    for (const [task, title] of Object.entries(TASKS)) {
      const ms = list.filter(m => m.task === task);
      if (!ms.length) continue;
      const box = h('div.aim-group', null, h('div.section-title', null, title));
      for (const m of ms) {
        const s = st[m.id] || {}, j = s.job;
        const size = m.convert ? m.convert.from.reduce((a, f) => a + (f.size || 0), 0) : m.files.reduce((a, f) => a + (f.size || 0), 0);
        let state;
        if (s.installed) state = `설치됨 ${mb(s.bytes)}`;
        else if (j && j.state === 'download') state = `받는 중 ${j.total ? Math.round(j.done / j.total * 100) : 0}%`;
        else if (j && j.state === 'convert') state = '변환 중…';
        else if (j && j.state === 'error') state = `실패: ${j.error}`;
        else state = m.manual ? '직접 받아 추가' : `받기 전 ${mb(size)}${m.convert ? ' (받은 뒤 변환)' : ''}`;
        const acts = h('div.aim-acts');
        if (!s.installed && !m.manual && !(j && ['download', 'convert'].includes(j.state))) acts.append(h('button.btn.primary', { type: 'button', onclick: () => fetchOne(m.id) }, '받기'));
        if (m.manual && !s.installed) acts.append(h('button.btn', { type: 'button', onclick: async () => {
          const f = await pickFile('.onnx');
          if (!f) return;
          await app.task('모델 파일 추가', () => server.put('models', `${m.id}/${m.files[0].name}`, f), { delay: 0 });
          app.toast(`${m.name} 을(를) 추가했습니다`);
          render();
        } }, '파일로 추가…'));
        if (s.installed) acts.append(h('button.btn', { type: 'button', onclick: async () => {
          if (!(await confirmDialog(`${m.name} 모델 파일을 지울까요? (휴지통 data/trash 로 옮깁니다)`, '지우기'))) return;
          closeAll();
          for (const f of m.files) await server.del('models', `${m.id}/${f.name}`).catch(() => {});
          render();
        } }, '지우기'));
        acts.append(h('a.btn', { href: m.source, target: '_blank', rel: 'noopener' }, '출처'));
        box.append(h('div.aim-row', null,
          h('div.aim-name', null, h('b', null, m.name), h('small', null, `${m.license}${m.note ? ' — ' + m.note : ''}`)),
          h(`span.aim-badge.${m.commercial}`, null, BADGE[m.commercial]),
          h('span.aim-state', null, state), acts));
      }
      body.append(box);
    }
    clearTimeout(timer);
    if (busy) timer = setTimeout(render, 1200);
  };
  await render();
  openModal({ title: 'AI 모델 관리', body, wide: true, onClose: () => clearTimeout(timer), actions: [{ label: '닫기', primary: true }] });
}

// ---------------------------------------------------------------------
// AI 기능 고르기 (처음 세팅·AI 메뉴): 기능 단위로 고르면 알맞은 상업 가능 모델을 받는다
// ---------------------------------------------------------------------
export function aiBundles(env = {}) {
  return [
    { id: 'pose', name: '사진 → 인형 자세·손 모양', desc: '3D 소재 창에서 사진 한 장으로 데생 인형 자세 잡기', models: ['pose-mp', 'hand-mp'], on: true },
    env.torch
      ? { id: 'lineart', name: '선화 추출 (만화용)', desc: '채색 그림·스캔 원고에서 선만 뽑기', models: ['line-manga'], on: true }
      : { id: 'lineart', name: '선화 추출', desc: '채색 그림·사진에서 선만 뽑기 (만화 전용 모델은 PyTorch 가 있으면 AI 모델 관리에서)', models: ['line-informative'], on: true },
    { id: 'face', name: '버튜버 얼굴 추적', desc: '웹캠으로 내 표정·고개 움직임을 그린 캐릭터에 입히기 (버튜버 모드)', models: ['face-mp'], on: false },
    { id: 'upscale', name: '크기 키우기 (2·4배)', desc: '작은 그림을 인쇄용으로 크게 (설치 파일에 들어 있어 바로 됨)', models: ['up-animevideo'], on: true },
    { id: 'cutout', name: '캐릭터 따기', desc: '애니·만화 캐릭터만 선택하거나 오려 내기', models: ['seg-anime'], on: false },
    { id: 'depth', name: '깊이 추정', desc: '명암·원근 참고용 깊이 그림', models: ['depth-s'], on: false },
    { id: 'select', name: '탭해서 고르기', desc: '탭한 물체를 선택 (MobileSAM, 설치 파일에 들어 있어 바로 됨)', models: ['sam-mobile'], on: true },
  ];
}

/** 고르기 화면 조각: { el, start() } — start() 는 고른 것을 받기 시작하고 고른 모델 수를 돌려줌 */
export async function aiPickSection() {
  const el = h('div.ai-pick');
  if (!server.http) {
    el.append(h('div.help', null, 'AI 기능은 ComicPad 서버(바탕화면 아이콘)로 켰을 때 AI > AI 모델 관리에서 받을 수 있습니다.'));
    return { el, start: async () => 0 };
  }
  const [list, st] = await Promise.all([registry(), modelStatus()]);
  const byId = Object.fromEntries(list.map(m => [m.id, m]));
  const bundles = aiBundles(st._env || {});
  const checks = new Map();
  const total = () => bundles.filter(b => checks.get(b.id).checked).flatMap(b => b.models).filter(id => !st[id]?.installed)
    .reduce((a, id) => a + ((byId[id].convert ? byId[id].convert.from : byId[id].files).reduce((x, f) => x + (f.size || 0), 0)), 0);
  const sum = h('div.help');
  const upd = () => { sum.textContent = `받을 크기: 약 ${mb(total()) || '0MB'} (data/models 에 저장, 인터넷 필요). 나중에 AI > AI 기능 고르기·받기 에서 더 받거나 지울 수 있습니다.`; };
  el.append(h('div.section-title', null, '쓸 AI 기능 고르기'),
    h('div.help', null, '고른 기능의 모델만 이 PC 에 받습니다. 모두 상업용으로 쓸 수 있는 모델이며(일부는 "확인 필요"), 비상업 모델은 AI 모델 관리에서 따로 받습니다.'));
  for (const b of bundles) {
    const ms = b.models.map(id => byId[id]).filter(Boolean);
    const have = ms.every(m => st[m.id]?.installed);
    const size = ms.filter(m => !st[m.id]?.installed).reduce((a, m) => a + (m.convert ? m.convert.from : m.files).reduce((x, f) => x + (f.size || 0), 0), 0);
    const c = h('input', { type: 'checkbox' });
    c.checked = have || b.on;
    c.disabled = have;
    c.onchange = upd;
    checks.set(b.id, c);
    const worst = ms.some(m => m.commercial === 'check') ? 'check' : 'yes';
    el.append(h('label.ai-pick-row', null, c,
      h('span', null, h('b', null, b.name), h('small', null, b.desc)),
      h(`span.aim-badge.${worst}`, null, BADGE[worst]),
      h('span.aim-state', null, have ? '받아 둠' : mb(size) || '작음')));
  }
  el.append(sum);
  upd();
  const start = async () => {
    const ids = bundles.filter(b => checks.get(b.id).checked && !checks.get(b.id).disabled).flatMap(b => b.models);
    for (const id of ids) await fetch(`/api/models/fetch?id=${encodeURIComponent(id)}`, { method: 'POST' }).catch(() => {});
    (app.settings.aiFeatures ||= []).push(...bundles.filter(b => checks.get(b.id).checked).map(b => b.id));
    app.settings.aiFeatures = [...new Set(app.settings.aiFeatures)];
    app.saveSettingsSoon();
    if (ids.length) app.toast(`AI 모델 ${ids.length}개를 받는 중입니다. 진행 상황은 AI > AI 모델 관리`, 'info', { duration: 7000, actions: [{ label: '보기', run: () => app.run('ai.models') }] });
    return ids.length;
  };
  return { el, start };
}

export async function aiSetupDialog() {
  const { el, start } = await aiPickSection();
  openModal({ title: 'AI 기능 고르기·받기', body: el, wide: true, actions: [{ label: '닫기' }, { label: '고른 것 받기', primary: true, run: () => start() }] });
}

export function initAI() {
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('ai.models', 'AI 모델 관리 (받기·라이선스)…', modelsDialog);
  C('ai.setup', 'AI 기능 고르기·받기…', aiSetupDialog);
  C('ai.lineart', 'AI 선화 추출…', aiLineart);
  C('ai.cutout', 'AI 캐릭터·배경 따기…', aiCutout);
  C('ai.select', 'AI 고르기 (탭한 물체 선택)', startAiSelect);
  C('ai.depth', 'AI 깊이 추정…', aiDepth);
  C('ai.upscale', 'AI 크기 키우기 (새 작품으로)…', aiUpscale);
  C('ai.pose', '사진에서 인형 자세 (3D 소재 창)…', () => { app.run('3d.open'); app.toast('3D 소재 창에서 인형을 고르고 "사진에서 자세 (AI)" 를 누르세요'); });
  app.registerTool(aiSelectTool);
  initVP();
  app.addMenuItem('ruler', { command: 'ruler.fromPhoto' });
  app.addMenu('ai', 'AI');
  // 도움말 메뉴 앞에 두기
  const ord = app.menuOrder, i = ord.indexOf('ai'), hp = ord.indexOf('help');
  if (hp >= 0 && i > hp) { ord.splice(i, 1); ord.splice(hp, 0, 'ai'); }
  for (const it of [{ command: 'ai.lineart' }, { command: 'ai.cutout' }, { command: 'ai.select' }, { command: 'ai.depth' }, { command: 'ai.upscale' }, { command: 'ai.pose' }, { command: 'ruler.fromPhoto' },
    { sep: true }, { command: 'filter.lineClean' }, { command: 'ai.img2img' }, { command: 'ai.check' }, { sep: true }, { command: 'ai.setup' }, { command: 'ai.models' }]) app.addMenuItem('ai', it);
  app.bus.on('doc:changed', () => syncNcBadge());
  setTimeout(() => { wrapExports(); syncNcBadge(); }, 0);          // 다른 명령이 다 등록된 뒤
}
