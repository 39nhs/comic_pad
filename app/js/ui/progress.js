// 진행표: 페이지마다 단계(콘티→선화→채색→완료)·작업 시간·마감일. 작업 시간은 그리는 동안 자동으로 잰다
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';
import { storyOf, listPages, readStoryInfo, writeStoryInfo, gotoPage } from './story.js';

export const STAGES = [['', '-'], ['conti', '콘티'], ['line', '선화'], ['color', '채색'], ['done', '완료']];
const COLORS = { conti: '#8e5bd6', line: '#3d7be0', color: '#f08c2e', done: '#2fa35b' };
const IDLE = 120;   // 이 시간(초) 넘게 손을 놓으면 쉬는 시간으로 보고 세지 않음

export const fmtTime = (sec) => { sec = Math.round(sec || 0); const hh = Math.floor(sec / 3600), mm = Math.floor((sec % 3600) / 60); return hh ? `${hh}시간 ${mm}분` : `${mm}분`; };

let last = 0;
function tick() {
  const doc = app.doc;
  if (!doc) return;
  const now = Date.now() / 1000;
  if (last && now - last < IDLE) doc.workTime = (doc.workTime || 0) + (now - last);
  last = now;
  syncStatus();
}

let statusEl = null;
function syncStatus() {
  if (!statusEl) {
    const bar = document.getElementById('statusbar');
    if (!bar) return;
    statusEl = h('span.work-time', { title: '이 그림에 들인 작업 시간 (2분 넘게 쉬면 멈춤)', onclick: () => app.run('progress.open') });
    bar.append(statusEl);
  }
  statusEl.textContent = `⏱ ${fmtTime(app.doc?.workTime)}`;
}

async function onSaved(doc) {
  const story = storyOf(doc?.file);
  if (!story) return;
  const info = await readStoryInfo(story);
  info.time = { ...(info.time || {}), [doc.file]: Math.round(doc.workTime || 0) };
  await writeStoryInfo(story, info);
}

export async function progressDialog() {
  const doc = app.doc;
  const story = storyOf(doc?.file);
  if (!story) {
    openModal({ title: '진행표', body: h('div.help-text', null,
      h('p', null, `이 그림의 작업 시간: ${fmtTime(doc?.workTime)}`),
      h('p', null, '여러 페이지 작품(만화 메뉴 > 새 작품)을 만들면 페이지마다 단계·시간·마감일을 표로 관리할 수 있습니다.')), actions: [{ label: '닫기', primary: true }] });
    return;
  }
  const pages = await listPages(story);
  const info = await readStoryInfo(story);
  info.status ||= {}; info.time ||= {};
  if (doc.file) info.time[doc.file] = Math.round(doc.workTime || 0);
  let modal;
  const body = h('div');
  const save = async () => { await writeStoryInfo(story, info); render(); };
  const render = () => {
    body.innerHTML = '';
    const n = pages.length;
    const count = (k) => pages.filter(p => (info.status[p.name] || '') === k).length;
    const done = count('done');
    const total = pages.reduce((s, p) => s + (info.time[p.name] || 0), 0);
    const doneTime = pages.filter(p => info.status[p.name] === 'done').reduce((s, p) => s + (info.time[p.name] || 0), 0);
    const per = done ? doneTime / done : 0;
    const bar = h('div.progress-bar', null, ...['done', 'color', 'line', 'conti'].map(k => h('i', { style: { width: `${count(k) / n * 100}%`, background: COLORS[k] }, title: `${STAGES.find(s => s[0] === k)[1]} ${count(k)}쪽` })));
    let dday = '';
    if (info.deadline) {
      const days = Math.ceil((new Date(info.deadline + 'T23:59:59') - new Date()) / 86400000);
      dday = days >= 0 ? `마감까지 ${days}일` : `마감 ${-days}일 지남`;
      if (days > 0 && n - done > 0) dday += ` · 하루 ${((n - done) / days).toFixed(1)}쪽씩`;
    }
    const dl = h('input', { type: 'date', value: info.deadline || '' });
    dl.onchange = () => { info.deadline = dl.value || null; save(); };
    body.append(
      h('div.row', null, h('b', null, `${story} · ${n}쪽 · 완료 ${done}쪽 (${Math.round(done / n * 100)}%)`)),
      bar,
      h('div.row', null, h('span', null, '마감일 '), dl, h('span', null, ` ${dday}`)),
      h('div.help', null, `작업 시간 합계 ${fmtTime(total)}${per ? ` · 완료한 쪽 평균 ${fmtTime(per)} → 남은 쪽 예상 ${fmtTime(per * (n - done))}` : ''}`),
    );
    const table = h('table.progress-table', null, h('tr', null, h('th', null, '쪽'), h('th', null, '단계'), h('th', null, '작업 시간'), h('th', null, '')));
    pages.forEach((p, i) => {
      const sel = h('select', null, ...STAGES.map(([v, t]) => h('option', { value: v }, t)));
      sel.value = info.status[p.name] || '';
      sel.onchange = () => { info.status[p.name] = sel.value || undefined; save(); };
      table.append(h('tr', null,
        h('td', null, `${i + 1}${p.name === doc.file ? ' (지금)' : ''}`),
        h('td', null, sel),
        h('td', null, fmtTime(info.time[p.name])),
        h('td', null, p.name === doc.file ? null : h('button.btn', { type: 'button', onclick: () => { modal.close(true); gotoPage(p.name); } }, '열기'))));
    });
    body.append(table, h('div.row', null,
      h('button.btn', { type: 'button', onclick: () => { for (const p of pages) if (!info.status[p.name]) info.status[p.name] = 'conti'; save(); } }, '빈 칸 모두 "콘티"로'),
      doc.file ? h('button.btn', { type: 'button', onclick: () => { info.status[doc.file] = 'done'; save(); } }, '지금 쪽 완료') : null));
  };
  render();
  modal = openModal({ title: '진행표', body, wide: true, actions: [{ label: '닫기', primary: true }] });
}

export function initProgress() {
  app.bus.on('history:changed', tick);
  app.bus.on('doc:changed', () => { last = 0; syncStatus(); });
  app.bus.on('doc:saved', onSaved);
  app.registerCommand('progress.open', { label: '진행표 (단계·작업 시간·마감)…', run: progressDialog });
  app.addMenuItem('comic', { command: 'progress.open' });
  setTimeout(syncStatus, 500);
}
