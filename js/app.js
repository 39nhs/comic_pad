// 앱 전역 상태와 확장 지점(레지스트리). 플러그인은 이 객체를 api 로 받는다.

class Bus {
  constructor() { this.map = new Map(); }
  on(evt, fn) {
    if (!this.map.has(evt)) this.map.set(evt, new Set());
    this.map.get(evt).add(fn);
    return () => this.map.get(evt).delete(fn);
  }
  emit(evt, data) {
    const set = this.map.get(evt);
    if (!set) return;
    for (const fn of [...set]) {
      try { fn(data); } catch (e) { console.error(`[${evt}]`, e); }
    }
  }
}

export const DEFAULT_SETTINGS = {
  pressureGamma: 1.0,          // 필압 곡선 (1 = 선형, <1 = 부드럽게, >1 = 딱딱하게)
  // 필기감: filter 'euro' = 1€ 필터 (느리면 많이, 빠르면 적게 보정), 'avg' = 예전 평균 방식
  // pressureSmooth 0·startRamp false = 펜이 보내는 필압 그대로 (클립스튜디오 기본과 같은 느낌)
  penFeel: { filter: 'euro', pressureSmooth: 0, startRamp: false, predict: true },
  brushCursor: 'nib',          // nib 펜촉+크기 원 | circle 크기 원 | shape 브러시 끝 모양 | cross 십자 | dot 점
  mousePressure: 1.0,
  fingerDraw: false,           // 손가락으로 그리기 (끄면 손가락은 화면 조작 전용)
  palmGuardMs: 350,            // 펜을 띄우고 이 시간 동안 터치 무시 (손바닥 오입력 방지)
  touchRotate: true,
  twoFingerUndo: true,
  threeFingerRedo: true,
  longPressEyedropper: true,
  penButton: 'eyedropper',     // 펜 측면 버튼: eyedropper | eraser | pan | none
  symmetry: 'none',            // none | v | h | vh
  showGrid: false,
  gridSize: 100,
  autosaveMin: 3,
  quickbar: { x: 12, y: 80, visible: true, vertical: true },
  dockSplit: 0.48,
  sideVisible: true,
  recentColors: [],
  palette: ['#000000', '#ffffff', '#3a3a3a', '#7a7a7a', '#bdbdbd', '#3a7bd5', '#e04646', '#f2a93b', '#f7e15a', '#4caf50', '#8e5bd6', '#f4c7b8'],
  brushes: null,               // 사용자 브러시 프리셋 (없으면 기본값)
  activePreset: {},
  keys: {},                    // 단축키 덮어쓰기 { commandId: 'ctrl+shift+x' }
};

export const app = {
  bus: new Bus(),
  doc: null,
  view: null,
  history: null,
  compositor: null,
  settings: structuredClone(DEFAULT_SETTINGS),
  color: { fg: '#1b1b1f', bg: '#ffffff' },
  // 화면 버튼으로 켜는 보조키 (키보드 없는 태블릿 모드용)
  modifiers: { line: false, snap: false, shift: false },

  tools: new Map(),
  tool: null,
  toolId: null,
  prevToolId: null,

  // 그리는 중인 스트로크 미리보기 { layer, canvas, mode, opacity }
  live: null,
  // 변형 중인 떠 있는 픽셀 { layer, canvas, matrix }
  floating: null,
  // 화면 위 오버레이 그리기 훅 (ctx, view) => void
  overlays: new Set(),

  commands: new Map(),
  menus: new Map(),       // menuId -> [{label, command|run, sep, ...}]
  menuOrder: [],
  panels: new Map(),      // 사이드 패널 탭 id -> {title, dock, mount(el)}
  quickActions: new Map(),

  _renderQueued: false,
  _renderFn: null,

  requestRender() {
    if (this._renderQueued || !this._renderFn) return;
    this._renderQueued = true;
    requestAnimationFrame(() => {
      this._renderQueued = false;
      this._renderFn();
    });
  },

  // ---- 도구 ----
  registerTool(tool) {
    this.tools.set(tool.id, tool);
    this.bus.emit('tools:registered', tool);
    return tool;
  },

  setTool(id) {
    if (!this.tools.has(id)) return;
    if (this.toolId === id) return;
    const prev = this.tool;
    if (prev?.deactivate) prev.deactivate();
    this.prevToolId = this.toolId;
    this.toolId = id;
    this.tool = this.tools.get(id);
    if (this.tool.activate) this.tool.activate();
    this.bus.emit('tool:changed', this.tool);
    this.requestRender();
  },

  // ---- 명령/메뉴 ----
  registerCommand(id, def) {
    this.commands.set(id, { id, ...def });
    this.bus.emit('commands:changed');
  },

  run(id, ...args) {
    const c = this.commands.get(id);
    if (!c) { console.warn('알 수 없는 명령', id); return; }
    if (c.enabled && !c.enabled()) return;
    try {
      const r = c.run(...args);
      if (r && r.catch) r.catch(e => this.toast(String(e.message || e), 'error'));
      return r;
    } catch (e) {
      console.error(e);
      this.toast(String(e.message || e), 'error');
    }
  },

  addMenu(id, label) {
    if (!this.menus.has(id)) { this.menus.set(id, { label, items: [] }); this.menuOrder.push(id); }
    return this.menus.get(id);
  },

  // item: { label, command } | { label, run } | { sep: true } | { label, submenu:[...] }
  addMenuItem(menuId, item) {
    const m = this.menus.get(menuId) || this.addMenu(menuId, menuId);
    m.items.push(item);
    this.bus.emit('menus:changed');
  },

  // ---- 사이드 패널 ----
  registerPanel(id, def) {
    this.panels.set(id, { id, dock: 'top', ...def });
    this.bus.emit('panels:changed');
  },

  // ---- 퀵바 버튼 ----
  registerQuickAction(id, def) {
    this.quickActions.set(id, { id, ...def });
    this.bus.emit('quick:changed');
  },

  // ---- 오버레이 ----
  addOverlay(fn) { this.overlays.add(fn); return () => this.overlays.delete(fn); },

  // ui 에서 덮어씀
  toast(msg, type = 'info') { console.log(type, msg); },

  setColor(hex, which = 'fg') {
    this.color[which] = hex;
    this.bus.emit('color:changed', this.color);
  },

  markModified() {
    if (this.doc && !this.doc.modified) {
      this.doc.modified = true;
      this.bus.emit('doc:modified', true);
    }
  },

  saveSettingsSoon() { this.bus.emit('settings:dirty'); },
};

window.comicpad = app; // 콘솔 디버깅용
