// ComicPad 시작점: 모듈 조립 + 플러그인 로드
import './core/net.js';
import { app } from './app.js';
import './ui/dialogs.js';
import './core/compositor.js';
import { View } from './core/view.js';
import { History } from './core/history.js';
import { initRenderer } from './core/renderer.js';
import { initInput } from './core/input.js';
import * as ops from './core/ops.js';
import * as util from './core/util.js';
import * as raster from './core/raster.js';
import * as brushCore from './core/brush.js';
import { server, loadSettings, saveSettings, autosave, saveAsset, extractContent } from './core/io.js';
import { BrushTool, activePreset } from './tools/brush.js';
import { fillTool, eyedropperTool, viewTool, helpText } from './tools/basic.js';
import { selectTool, selectionHistory } from './tools/select.js';
import { transformTool } from './tools/transform.js';
import { shapeTool } from './tools/shape.js';
import { panelTool, bubbleTool, textTool, drawBubble, renderText } from './tools/comic.js';
import { gradientTool, lassoFillTool, smudgeTool, selPenTool } from './tools/paint2.js';
import { rulerTool, addRuler, RulerSnap } from './tools/ruler.js';
import { registerMoreCommands } from './commands2.js';
import { initManuscript } from './ui/manuscript.js';
import { initPenDiagnostic } from './ui/pendiag.js';
import { vectorSelectTool } from './tools/vselect.js';
import { scriptTool } from './tools/script.js';
import { initPrintout } from './ui/printout.js';
import { initABR } from './ui/abrimport.js';
import { initTimelapse } from './ui/timelapse.js';
import { initWorkspaces } from './ui/workspace.js';
import { initFlats } from './ui/flats.js';
import { initViewAids } from './ui/viewaids.js';
import { initProgress } from './ui/progress.js';
import { initWebtoonExport } from './ui/webtoonexport.js';
import { initRadial } from './ui/radial.js';
import { initFeedback } from './ui/feedback.js';
import { initAITools } from './ui/aitools.js';
import { initAI } from './ui/aimodels.js';
import { initBackup } from './ui/backup.js';
import { initUpdate } from './ui/update.js';
import { initReport } from './ui/report.js';
import { initCursor } from './ui/cursor.js';
import { initVtuber } from './ui/vtuber.js';
import { initPSTools } from './ui/pstools.js';
import { initSfx } from './tools/sfx.js';
import { initPreflight } from './ui/preflight.js';
import { initPanelTemplates } from './ui/paneltemplates.js';
import { initPixel } from './ui/pixel.js';
import { initMemSave } from './core/memsave.js';
import { initLayout } from './ui/layout.js';
import { initPopout } from './ui/popout.js';
import { initSelfTest } from './ui/selftest.js';
import { initGuide } from './ui/guide.js';
import { initSetupPresets } from './ui/setuppresets.js';
import { navigatorPanel, historyPanel, openReference } from './ui/extra.js';
import { writePSD } from './core/psd.js';
import { readPSD } from './core/psdread.js';
import * as anim from './core/anim.js';
import { registerAnimCommands, timelinePanel } from './ui/anim.js';
import { cloneTool, blurTool, liquifyTool, speedLinesTool, flashDialog } from './tools/retouch.js';
import { registerAdjustCommands, adjustDialog, addAdjustLayer, fxDialog, toneDialog } from './ui/adjust.js';
import { pagesPanel, registerStoryCommands } from './ui/story.js';
import { registerPaletteCommands, openPalette } from './ui/palette.js';
import * as filters from './core/filters.js';
import { server as io_server } from './core/io.js';
import { registerCommands, registerMenus, DEFAULT_KEYS } from './commands.js';
import { registerSettingsCommands, initShortcuts } from './ui/settings.js';
import { initToolbar, initDocks, initMenubar, initTopActions, initStatusbar, toolPropsPanel, colorPanel, layersPanel, assetsPanel, showDockTab } from './ui/panels.js';
import { initQuickbar, registerDefaultQuickActions } from './ui/quickbar.js';
import { initBottombar, registerBottomActions } from './ui/bottombar.js';
import { register3DCommands, open3D } from './ui/scene3d.js';
import { initMobile } from './ui/mobile.js';
import { newDocDialog, createFromPreset, openAnyFile } from './ui/files.js';
import * as controls from './ui/controls.js';
import { icon, addIcon } from './ui/icons.js';
import { showBar, hideBar, registerSelectionAction } from './ui/bars.js';

function mergeSettings(saved) {
  if (!saved || typeof saved !== 'object') return;
  for (const [k, v] of Object.entries(saved)) {
    if (v && typeof v === 'object' && !Array.isArray(v) && app.settings[k] && typeof app.settings[k] === 'object' && !Array.isArray(app.settings[k])) {
      app.settings[k] = { ...app.settings[k], ...v };
    } else app.settings[k] = v;
  }
  // 1.1.3: 예전 기본 필압 보정(필압 다듬기 35%·시작 굵기 줄이기)을 그대로 쓰던 경우만 새 기본으로 (직접 바꾼 값은 그대로)
  if ((app.settings.feelV || 0) < 2) {
    const f = app.settings.penFeel || {};
    if (f.pressureSmooth === 0.35) f.pressureSmooth = 0;
    if (f.startRamp === true) f.startRamp = false;
    app.settings.penFeel = f;
    if (!saved.brushCursor || saved.brushCursor === 'circle') app.settings.brushCursor = 'nib';
    app.settings.feelV = 2;
  }
}

async function loadFonts() {
  app.fonts = [];
  try {
    const list = (await server.list('fonts')).filter(f => /\.(ttf|otf|woff2?)$/i.test(f.name));
    for (const f of list) {
      const name = f.name.replace(/\.[^.]+$/, '').split('/').pop();
      try {
        const face = new FontFace(name, `url("${await server.src('fonts', f.name)}")`);
        await face.load();
        document.fonts.add(face);
        app.fonts.push(name);
      } catch (e) { console.warn('폰트 로드 실패', f.name, e); }
    }
  } catch { /* 폰트 폴더 없음 */ }
}

/** 플러그인에게 넘겨주는 도구 상자 */
export const kit = {
  ops, util, raster, brush: brushCore, controls, icon, addIcon, showBar, hideBar,
  BrushTool, activePreset, selectionHistory, helpText, drawBubble, renderText,
  saveAsset, extractContent, server, registerSelectionAction, showDockTab,
  addRuler, RulerSnap, openReference, writePSD, readPSD, anim,
  filters, adjustDialog, addAdjustLayer, fxDialog, toneDialog, openPalette,
};

async function loadPlugins() {
  let list = [];
  try { list = (await server.list('plugins')).filter(p => /^[^/_][^/]*\.js$/.test(p.name)).sort((a, b) => a.name.localeCompare(b.name)); } catch { return; }
  app.plugins = [];
  for (const p of list) {
    try {
      const mod = await import(server.mode === 'http' ? server.url('plugins', p.name) + '?v=' + p.mtime : await server.src('plugins', p.name));
      const setup = mod.default || mod.setup;
      if (typeof setup !== 'function') throw new Error('export default function(app, kit) 가 없습니다');
      const info = (await setup(app, kit)) || {};
      app.plugins.push({ file: p.name, ...info });
    } catch (e) {
      console.error('플러그인 오류', p.name, e);
      app.toast(`플러그인 오류: ${p.name} — ${e.message}`, 'error');
    }
  }
  if (app.plugins.length) {
    app.addMenuItem('plugin', { label: `불러온 플러그인: ${app.plugins.map(p => p.name || p.file).join(', ')}`, run: () => {} });
  }
}

// 어디서든 오류가 나면 앱을 멈추지 않고 알림만
let lastErr = '';
function reportError(msg) {
  if (!msg || msg === lastErr) return;
  lastErr = msg;
  setTimeout(() => { lastErr = ''; }, 3000);
  app.toast('오류: ' + msg, 'error');
}
// 최근 오류는 "문제 보내기" 에 붙일 수 있게 기억 (최근 30개, 이 화면 안에서만)
app.errors = [];
const keepErr = (msg, stack) => { app.errors.push({ t: new Date().toISOString(), msg: String(msg).slice(0, 500), stack: String(stack || '').slice(0, 1500) }); app.errors.splice(0, app.errors.length - 30); };
window.addEventListener('error', (e) => { console.error(e.error || e.message); keepErr(e.message, e.error?.stack); reportError(e.message); });
window.addEventListener('unhandledrejection', (e) => { console.error(e.reason); keepErr(e.reason?.message || String(e.reason), e.reason?.stack); reportError(e.reason?.message || String(e.reason)); });

/** 서버가 없을 때 어디에 저장되는지 알려 주고, 폴더를 다시 허락받을 수 있게 */
async function storageNotice() {
  const S = await import('./core/storage.js');
  if (server.mode === 'folder' && S.driveMirror.state === 'prompt') {
    app.toast(`구글 드라이브 사본 폴더 "${S.driveMirror.root.name}" 를 다시 쓰려면 허락해 주세요`, 'warn', { duration: 0, actions: [{ label: '허락', run: () => S.driveMirror.reconnect() }] });
  }
  if (server.mode !== 'device') return;
  if (S.folderStore.state === 'prompt') {
    app.toast(`서버 없이 실행 중입니다. "${S.folderStore.root.name}" 폴더를 다시 쓰려면 허락해 주세요 (그 전까지는 이 기기 안에 저장)`, 'warn', {
      duration: 0,
      actions: [{ label: '폴더 허락', run: async () => { if (await S.folderStore.reconnect()) await server.chooseMode(); } }],
    });
  } else if (S.folderSupported() && !server.remote) {
    app.toast('PC 서버 없이 실행 중입니다. 지금은 이 기기(브라우저) 안에 저장됩니다.', 'warn', {
      duration: 12000,
      actions: [{ label: 'G: 폴더에 저장하기', run: () => app.run('file.storage') }],
    });
  } else {
    app.toast('오프라인: 작업은 이 기기 안에 저장됩니다 (파일 > 기기 임시 저장함)', 'info', { duration: 5000 });
  }
}

async function boot() {
  await server.ping();
  // 비정상 종료 감지 (창이 정상적으로 닫히면 0 으로 바뀜)
  let crashed = false;
  try { crashed = localStorage.getItem('comicpad.alive') === '1'; localStorage.setItem('comicpad.alive', '1'); } catch { /* 무시 */ }
  window.addEventListener('pagehide', () => { try { localStorage.setItem('comicpad.alive', '0'); } catch { /* 무시 */ } });
  mergeSettings(await loadSettings());
  app.defaultKeys = DEFAULT_KEYS;
  app.view = new View();
  app.history = new History();

  const s = app.settings;
  document.body.classList.toggle('large-ui', !!s.largeUI);
  document.body.classList.toggle('left-handed', !!s.leftHanded);
  document.body.classList.toggle('no-side', s.sideVisible === false);

  // 도구
  app.registerTool(Object.assign(new BrushTool({ id: 'brush', name: '브러시', icon: 'pen', key: 'b', category: 'brush', mode: 'paint' }), { altKeys: ['p'] }));
  app.registerTool(new BrushTool({ id: 'eraser', name: '지우개', icon: 'eraser', key: 'e', category: 'eraser', mode: 'erase' }));
  for (const t of [fillTool, eyedropperTool, selectTool, transformTool, shapeTool, panelTool, bubbleTool, textTool, viewTool, gradientTool, lassoFillTool, smudgeTool, selPenTool, rulerTool, cloneTool, blurTool, liquifyTool, speedLinesTool, vectorSelectTool, scriptTool]) app.registerTool(t);

  registerCommands();
  registerSettingsCommands();
  registerMenus();
  registerMoreCommands();
  initManuscript();
  initPenDiagnostic();
  initPrintout();
  initABR();
  initTimelapse();
  initWorkspaces();
  initFlats();
  initViewAids();
  initProgress();
  initWebtoonExport();
  initRadial();
  initFeedback();
  initAITools();
  initAI();
  initBackup();
  initUpdate();
  initReport();
  initVtuber();
  initPSTools();
  initSfx();
  initPreflight();
  initPanelTemplates();
  initPixel();
  initMemSave();
  initLayout();
  initPopout();
  initSelfTest();
  initGuide();
  initSetupPresets();
  for (const it of [{ sep: true }, { command: 'print.setup' }, { command: 'print.toggle' }, { command: 'memo.tool' }]) app.addMenuItem('comic', it);
  for (const it of [{ sep: true }, { command: 'view.webtoon' }]) app.addMenuItem('view', it);
  registerAnimCommands();
  registerAdjustCommands();
  registerStoryCommands();
  registerPaletteCommands();
  register3DCommands();
  app.registerCommand('effect.flash', { label: '플래시 효과 (우니/베타)…', run: flashDialog });
  for (const it of [{ sep: true }, { label: '유선 도구', run: () => app.setTool('speedlines') }, { command: 'effect.flash' }]) app.addMenuItem('comic', it);
  registerDefaultQuickActions();
  registerBottomActions();

  app.registerPanel('tool', { title: '도구', icon: 'brush', dock: 'top', mount: toolPropsPanel });
  app.registerPanel('color', { title: '색', icon: 'palette', dock: 'top', mount: colorPanel });
  app.registerPanel('nav', { title: '내비', icon: 'nav', dock: 'top', mount: navigatorPanel });
  app.registerPanel('pages', { title: '페이지', icon: 'pages', dock: 'top', mount: pagesPanel });
  app.registerPanel('layers', { title: '레이어', icon: 'layers', dock: 'bottom', mount: layersPanel });
  app.registerPanel('assets', { title: '에셋', icon: 'star', dock: 'bottom', mount: assetsPanel });
  app.registerPanel('history', { title: '기록', icon: 'history', dock: 'bottom', mount: historyPanel });
  app.registerPanel('anim', { title: '애니', icon: 'film', dock: 'bottom', mount: timelinePanel });

  const stage = document.getElementById('stage');
  initRenderer(document.getElementById('view'), stage);
  initInput(stage);
  initCursor(stage);
  initShortcuts();

  // 첫 문서
  const last = s.lastNew;
  createFromPreset(last ? { name: '새 작품', ...last } : { name: '새 작품', w: 2480, h: 3508 });
  app.doc.modified = false;

  initToolbar();
  initDocks();
  initMenubar();
  initTopActions();
  initStatusbar();
  initQuickbar();
  initBottombar();
  initMobile();
  // 아래 막대가 생겼으므로 왼쪽 떠 있는 퀵바는 한 번 꺼 둔다 (보기 메뉴에서 다시 켤 수 있음)
  if (!s.bottomMigrated) { s.bottomMigrated = true; s.quickbar.visible = false; app.bus.emit('quick:changed'); app.saveSettingsSoon(); }
  app.setTool('brush');

  // 설정 저장
  const saveSoon = util.debounce(() => saveSettings(app.settings).catch(e => console.warn(e)), 800);
  app.bus.on('settings:dirty', saveSoon);
  window.addEventListener('pagehide', () => saveSoon.flush());

  // 레이어를 바꾸면 진행 중인 변형 확정
  app.bus.on('layers:active', () => { if (app.floating) app.tools.get('transform').commitPending(); });

  // 자동 저장 / 서버 확인
  let lastAuto = Date.now();
  setInterval(() => {
    const m = app.settings.autosaveMin;
    if (m > 0 && Date.now() - lastAuto > m * 60000) { lastAuto = Date.now(); autosave(); }
  }, 20000);
  setInterval(() => server.ping().then(() => app.bus.emit('settings:changed')), 60000);

  // 전용 창(ComicPad.exe)이 닫을 때 묻는 것: 저장 안 한 변경이 있는지 / 저장하기
  window.comicpadUnsaved = () => !!app.doc?.modified;
  window.comicpadSave = () => app.run('file.save');
  window.addEventListener('beforeunload', (e) => {
    if (app.doc?.modified) { e.preventDefault(); e.returnValue = ''; }
  });

  // 파일을 창에 끌어다 놓기: PSD/.cpad 는 열기, 이미지는 새 레이어로
  // 브라우저 기본 오른쪽 클릭 메뉴(뒤로·새로고침·검사 등)는 앱 안에서 띄우지 않음 — 앱의 원형 메뉴와 겹쳐 뜨지 않게.
  // 글자 입력칸에서는 복사·붙여넣기 메뉴가 필요하니 그대로 둔다.
  const noNativeMenu = (doc) => doc.addEventListener('contextmenu', (e) => {
    if (e.target?.closest?.('input, textarea, [contenteditable=""], [contenteditable="true"]')) return;
    e.preventDefault();
  }, { capture: true });
  noNativeMenu(document);
  app.noNativeMenu = noNativeMenu;
  window.addEventListener('dragover', (e) => e.preventDefault());
  window.addEventListener('drop', async (e) => {
    e.preventDefault();
    for (const f of e.dataTransfer?.files || []) {
      try { await openAnyFile(f, { asLayer: !/\.(psd|cpad|json)$/i.test(f.name) }); } catch (err) { app.toast(err.message, 'error'); }
    }
  });

  await loadFonts();
  await loadPlugins();
  // 저장소가 바뀌면(서버가 꺼졌다 켜짐, 폴더 허락) 글꼴을 다시 읽음
  app.bus.on('storage:changed', () => loadFonts());
  app.registerCommand('font.add', {
    label: '글꼴 파일 추가 (.ttf/.otf/.woff)…',
    run: async () => {
      const f = await util.pickFile('.ttf,.otf,.woff,.woff2');
      if (!f) return;
      await server.put('fonts', f.name, f);
      await loadFonts();
      app.toast(`글꼴 추가: ${f.name.replace(/\.[^.]+$/, '')} (텍스트 글꼴 목록 아래쪽)`);
    },
  });
  app.addMenuItem('file', { command: 'font.add' });

  storageNotice();
  app.bus.on('storage:changed', (mode) => app.toast({ http: 'PC 서버에 연결되었습니다', folder: '서버 없이 폴더에 직접 저장합니다', device: '이 기기 안에 저장합니다' }[mode]));
  // 다른 창으로 가거나 최소화할 때도 자동 저장
  document.addEventListener('visibilitychange', () => { if (document.hidden && app.settings.autosaveMin > 0) autosave(); else if (!document.hidden) import('./core/io.js').then(m => m.checkExternalChange()); });

  if (!location.hash.includes('nostart')) newDocDialog({ startup: true });
  if (crashed && server.online) {
    try {
      const list = (await io_server.list('autosave')).filter(i => i.name.endsWith('.cpad'));
      if (list.length && await app.ui.confirm(`지난번에 ComicPad 가 정상적으로 닫히지 않았습니다.\n자동 저장본 "${list[0].name}" (${new Date(list[0].mtime * 1000).toLocaleString()}) 을 열까요?`, '복구해서 열기')) {
        document.querySelectorAll('.modal-backdrop').forEach(m => m.remove());
        await app.run('file.recover');
      }
    } catch (e) { console.warn(e); }
  }
}

boot().catch((e) => {
  console.error(e);
  const pre = document.createElement('pre');
  pre.style.cssText = 'position:fixed;inset:auto 0 0 0;background:#400;color:#fff;padding:12px;z-index:99;white-space:pre-wrap';
  pre.textContent = `시작 오류: ${e.stack || e}`;
  document.body.append(pre);
});
