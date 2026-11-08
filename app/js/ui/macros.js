// 자동 액션 (매크로): 명령과 대화상자 값을 기록했다가 다시 실행. 여러 쪽·여러 작품에 일괄 적용.
import { app } from '../app.js';
import { h, uid, canvasToBlob, sleep } from '../core/util.js';
import { form, openModal, confirmDialog } from './dialogs.js';
import { server, saveProject, openProject } from '../core/io.js';
import { storyOf, listPages } from './story.js';

// 기록하지 않는 명령: 화면 보기·도움말·기록 자체 등 (그림을 바꾸지 않는 것)
const SKIP = /^(macro\.|view\.|help\.|settings\.|palette\.|ui\.|workspace\.|popout\.|selftest\.|setup\.|file\.(open|save|saveAs|new|recover|versions|storage|folder)|story\.(prev|next)|anim\.(play|prev|next)|ai\.models|edit\.(undo|redo))/;

let rec = null;          // 기록 중: { steps: [] }
const macros = () => (app.settings.macros ||= []);

function startRecord() {
  rec = { steps: [] };
  app.toast('자동 액션 기록 중… 메뉴 명령·필터·보정을 실행하세요. 끝나면 "기록 멈추기"', 'info', { duration: 6000 });
  app.bus.emit('macro:changed');
}

async function stopRecord() {
  const r = rec;
  rec = null;
  app.bus.emit('macro:changed');
  if (!r?.steps.length) { app.toast('기록된 명령이 없습니다'); return; }
  const v = await form('자동 액션 저장', [
    { key: 'name', label: '이름', type: 'text', value: `액션 ${macros().length + 1}` },
    { key: 'info', type: 'info', label: r.steps.map((s, i) => `${i + 1}. ${app.commands.get(s.cmd)?.label || s.cmd}${s.forms.length ? ' (설정값 포함)' : ''}`).join('\n') },
  ], { okLabel: '저장' });
  if (!v) return;
  macros().push({ id: uid('mc'), name: v.name || '액션', steps: r.steps });
  app.saveSettingsSoon();
  app.toast(`"${v.name}" 자동 액션을 저장했습니다 (편집 > 자동 액션)`);
}

/** 기록: app.run 을 감싸서 명령을, form:submit 으로 그 명령의 대화상자 값을 모은다 */
function installRecorder() {
  const run = app.run.bind(app);
  let depth = 0;
  app.run = (id, ...args) => {
    const top = rec && depth === 0 && !SKIP.test(id) && app.commands.has(id);
    let step = null;
    if (top) {
      const safeArgs = args.filter(a => a == null || ['string', 'number', 'boolean'].includes(typeof a));
      step = { cmd: id, args: safeArgs, forms: [] };
      rec.steps.push(step);
      rec.current = step;
    }
    depth++;
    try {
      const r = run(id, ...args);
      if (r && r.then) return r.finally(() => { depth--; });
      depth--;
      return r;
    } catch (e) { depth--; throw e; }
  };
  app.bus.on('form:submit', ({ title, values }) => {
    if (rec?.current) rec.current.forms.push({ title, values: JSON.parse(JSON.stringify(values)) });
  });
}

/** 한 번 실행 (대화상자는 기록한 값으로 자동 확인) */
export async function playMacro(m) {
  for (const step of m.steps) {
    if (!app.commands.has(step.cmd)) { app.toast(`없는 명령이라 건너뜀: ${step.cmd}`, 'warn'); continue; }
    const queue = [...step.forms];
    app.formAuto = (title) => {
      const i = queue.findIndex(f => f.title === title);
      if (i < 0) return null;
      return queue.splice(i, 1)[0].values;
    };
    try { await app.run(step.cmd, ...(step.args || [])); } finally { app.formAuto = null; }
    await sleep(30);
  }
}

async function batchDialog(m) {
  const story = storyOf(app.doc?.file);
  const all = await server.list('projects').catch(() => []);
  const folders = [...new Set(all.filter(i => i.name.endsWith('.cpad')).map(i => (i.name.includes('/') ? i.name.split('/')[0] : '')))];
  const v = await form(`일괄 적용: ${m.name}`, [
    { key: 'where', label: '대상', type: 'select', options: [...(story ? [[`story:${story}`, `지금 작품의 모든 쪽 (${story})`]] : []), ...folders.map(f => [`folder:${f}`, f ? `작품 폴더: ${f}` : '맨 위 폴더의 그림들'])], value: story ? `story:${story}` : `folder:${folders[0] ?? ''}` },
    { key: 'out', label: '결과', type: 'segment', options: [['export', 'PNG 로 내보내기 (원본 그대로)'], ['save', '작품에 저장 (덮어쓰기, 버전 기록 남음)']], value: 'export' },
  ], { okLabel: '실행' });
  if (!v) return;
  const [kind, name] = v.where.split(/:(.*)/s);
  const files = kind === 'story' ? (await listPages(name)).map(p => p.name) : all.filter(i => i.name.endsWith('.cpad') && (name ? i.name.startsWith(name + '/') : !i.name.includes('/'))).map(i => i.name);
  if (!files.length) { app.toast('대상 파일이 없습니다'); return; }
  if (v.out === 'save' && !(await confirmDialog(`${files.length}개 파일에 "${m.name}" 을(를) 적용하고 저장할까요? (이전 판은 버전 기록에 남습니다)`, '적용하고 저장'))) return;
  if (app.doc?.modified) await saveProject();
  const back = app.doc?.file;
  let n = 0;
  await app.task(`일괄 적용 (${files.length}개)`, async () => {
    for (const f of files) {
      await openProject(f);
      await playMacro(m);
      if (v.out === 'save') await saveProject();
      else {
        const c = app.compositor.flatten(app.doc, { background: '#ffffff', paper: true });
        await server.put('exports', `액션_${m.name}/${f.replace(/\.cpad$/, '').replace(/\//g, '_')}.png`, await canvasToBlob(c, 'image/png'));
        app.doc.modified = false;
      }
      n++;
    }
  }, { delay: 0 });
  if (back) await openProject(back).catch(() => {});
  app.toast(`${n}개 파일에 적용했습니다${v.out === 'export' ? ` (data/exports/액션_${m.name})` : ''}`);
}

export function macrosDialog() {
  const body = h('div.macros');
  let modal = null;
  const render = () => {
    body.innerHTML = '';
    body.append(h('div.help', null, '메뉴 명령·필터·보정을 순서대로 기록했다가 한 번에 다시 실행합니다. 대화상자에 넣은 값도 함께 기록됩니다. 그리기(펜 획)는 기록되지 않습니다.'));
    body.append(h('div.row', null, rec
      ? h('button.btn.primary', { type: 'button', onclick: () => { modal?.close(); stopRecord(); } }, `기록 멈추기 (${rec.steps.length}개)`)
      : h('button.btn.primary', { type: 'button', onclick: () => { modal?.close(); startRecord(); } }, '● 새로 기록')));
    if (!macros().length) body.append(h('div.help', null, '저장된 자동 액션이 없습니다.'));
    for (const m of macros()) {
      body.append(h('div.macro-row', null,
        h('div', null, h('b', null, m.name), h('small', null, m.steps.map(s => app.commands.get(s.cmd)?.label || s.cmd).join(' → '))),
        h('div.row', null,
          h('button.btn', { type: 'button', onclick: async () => { modal?.close(); await app.task(`자동 액션: ${m.name}`, () => playMacro(m), { delay: 0 }); app.toast(`"${m.name}" 실행 완료`); } }, '실행'),
          h('button.btn', { type: 'button', onclick: () => { modal?.close(); batchDialog(m); } }, '일괄…'),
          h('button.btn', { type: 'button', onclick: async () => { const v = await form('이름 바꾸기', [{ key: 'n', label: '이름', type: 'text', value: m.name }]); if (v?.n) { m.name = v.n; app.saveSettingsSoon(); } render(); } }, '이름'),
          h('button.btn', { type: 'button', onclick: async () => { if (await confirmDialog(`"${m.name}" 을(를) 지울까요?`, '지우기')) { macros().splice(macros().indexOf(m), 1); app.saveSettingsSoon(); render(); } } }, '지우기'))));
    }
  };
  render();
  modal = openModal({ title: '자동 액션', body, wide: true, actions: [{ label: '닫기', primary: true }] });
}

export const isRecording = () => !!rec;

export function initMacros() {
  installRecorder();
  app.registerCommand('macro.open', { label: '자동 액션 (기록·실행·일괄)…', run: macrosDialog });
  app.registerCommand('macro.record', { label: '자동 액션 기록 시작/멈추기', run: () => (rec ? stopRecord() : startRecord()) });
  for (const it of [{ sep: true }, { command: 'macro.open' }, { command: 'macro.record' }]) app.addMenuItem('edit', it);
  // 기록 중 표시
  app.bus.on('macro:changed', () => {
    let b = document.getElementById('macro-rec');
    if (!rec) { b?.remove(); return; }
    if (!b) { b = h('button.macro-rec', { type: 'button', onclick: () => stopRecord() }, '● 기록 중 (누르면 멈춤)'); b.id = 'macro-rec'; document.body.append(b); }
  });
}
