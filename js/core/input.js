// 포인터 입력: 펜/마우스는 도구로, 손가락은 화면 조작(제스처)으로 보낸다.
import { openRadial } from '../ui/radial.js';
import { app } from '../app.js';
import { dist } from './util.js';

export const keys = { space: false, ctrl: false, alt: false, shift: false };

export function initInput(stage) {
  const touches = new Map();     // pointerId -> {sx, sy, x0, y0}
  let active = null;             // 진행 중인 펜/마우스/손가락 그리기 { id, mode, restoreTool, last }
  let gesture = null;
  let lastPen = -1e9;
  let penIsDown = false;

  // 도구에서 오류가 나도 그리기 상태가 굳지 않게 정리
  const callTool = (name, e) => {
    const t = app.tool;
    if (!t || !t[name]) return;
    try {
      const r = t[name](e);
      if (r && r.catch) r.catch(err => recover(t, err));
    } catch (err) { recover(t, err); }
  };
  function recover(t, err) {
    console.error(err);
    app.toast(`도구 오류 (${t.name}): ${err.message || err}`, 'error');
    try { t.cancel?.(); } catch { /* 무시 */ }
    try { t.busy = false; } catch { /* getter 만 있는 도구 */ }
    app.live = null;
    active = null;
    app.requestRender();
  }

  const capture = (id) => { try { stage.setPointerCapture(id); } catch { /* 이미 끝난 포인터 */ } };

  const local = (e) => {
    const r = stage.getBoundingClientRect();
    return { sx: e.clientX - r.left, sy: e.clientY - r.top };
  };

  // 필압이 늘 같은 값(0.5·1·0)으로만 오면: 태블릿 드라이버가 필압을 안 보내는 것 → 한 번 알려 줌
  let penStats = null, pressureWarned = false;
  const checkPenPressure = () => {
    const st = penStats;
    penStats = null;
    if (!st || st.n < 15 || st.vals.size > 1 || pressureWarned || app.settings.penNoPressure) return;
    const v = [...st.vals][0];
    if (![0, 0.5, 1].includes(v)) return;
    pressureWarned = true;
    app.toast('이 펜에서 필압 값이 오지 않습니다. 와콤·휴이온·XP-Pen 은 태블릿 드라이버 설정에서 "Windows Ink" 를 켜면 필압이 됩니다.', 'warn', {
      duration: 12000,
      actions: [{ label: '필압 없이 쓰기 (굵기 고정)', run: () => { app.settings.penNoPressure = true; app.saveSettingsSoon(); } }, { label: '펜 진단', run: () => app.run('pen.diagnose') }],
    });
  };

  const toolEvent = (e) => {
    const { sx, sy } = local(e);
    const d = app.view.toDoc(sx, sy);
    let pressure = 1;
    if (e.pointerType === 'pen') {
      // 필압을 못 보내는 펜(드라이버 설정·기종)은 설정에 따라 굵기 고정
      pressure = app.settings.penNoPressure ? (e.buttons ? 1 : 0) : e.pressure > 0 ? e.pressure : (e.buttons ? 0.05 : 0);
    } else if (e.pointerType === 'mouse') pressure = app.settings.mousePressure;
    // 기울기: 와콤·S펜·서피스 펜은 tiltX/Y, 애플 펜슬(Safari)은 altitude/azimuth 로 옴
    let tiltX = e.tiltX || 0, tiltY = e.tiltY || 0;
    if (!tiltX && !tiltY && e.altitudeAngle != null && e.altitudeAngle < Math.PI / 2 - 0.01) {
      const t = 1 / Math.tan(e.altitudeAngle), az = e.azimuthAngle || 0;
      tiltX = Math.atan(Math.cos(az) * t) * 180 / Math.PI;
      tiltY = Math.atan(Math.sin(az) * t) * 180 / Math.PI;
    }
    return {
      x: d.x, y: d.y, sx, sy, pressure,
      type: e.pointerType, button: e.button, buttons: e.buttons,
      shift: e.shiftKey || app.modifiers?.shift, alt: e.altKey, ctrl: e.ctrlKey || e.metaKey,
      tiltX, tiltY, twist: e.twist || 0, time: e.timeStamp,
    };
  };

  app.pointer = { sx: 0, sy: 0, x: 0, y: 0, inside: false, type: 'mouse' };
  const trackHover = (e) => {
    const te = toolEvent(e);
    Object.assign(app.pointer, { sx: te.sx, sy: te.sy, x: te.x, y: te.y, inside: true, type: e.pointerType });
    return te;
  };

  // ---- 임시 도구 전환 -------------------------------------------------
  function beginTemp(toolId) {
    if (app.toolId === toolId) return null;
    const prev = app.toolId;
    app.setTool(toolId);
    return prev;
  }

  function viewDrag(mode, te) {
    return { mode, sx0: te.sx, sy0: te.sy, last: te, m0: app.view.m };
  }

  function applyViewDrag(a, te) {
    const v = app.view;
    if (a.mode === 'pan') {
      v.panBy(te.sx - a.last.sx, te.sy - a.last.sy);
    } else if (a.mode === 'zoom') {
      const f = Math.exp((te.sx - a.last.sx) * 0.01);
      v.zoomAt(f, a.sx0, a.sy0);
    } else if (a.mode === 'rotate') {
      const cx = v.w / 2, cy = v.h / 2;
      const a0 = Math.atan2(a.last.sy - cy, a.last.sx - cx), a1 = Math.atan2(te.sy - cy, te.sx - cx);
      v.rotateAt((a1 - a0) * 180 / Math.PI);
    }
    a.last = te;
  }

  // ---- 제스처 ---------------------------------------------------------
  function centroid() {
    let x = 0, y = 0;
    for (const t of touches.values()) { x += t.sx; y += t.sy; }
    return { x: x / touches.size, y: y / touches.size };
  }

  function twoInfo() {
    const [a, b] = [...touches.values()];
    return { d: dist(a.sx, a.sy, b.sx, b.sy), ang: Math.atan2(b.sy - a.sy, b.sx - a.sx) * 180 / Math.PI };
  }

  function rebase() {
    if (!gesture || !touches.size) return;
    gesture.m0 = app.view.m;
    gesture.c0 = centroid();
    gesture.count = touches.size;
    if (touches.size >= 2) {
      const i = twoInfo();
      gesture.d0 = i.d; gesture.a0 = i.ang;
    }
    gesture.rotOn = gesture.rotOn && touches.size >= 2;
  }

  function startGesture() {
    clearTimeout(gesture?.lp);
    gesture = { t0: performance.now(), max: touches.size, moved: false, rotOn: false };
    rebase();
    if (touches.size === 1 && app.settings.longPressEyedropper && !app.settings.fingerDraw) {
      const [t] = touches.values();
      gesture.lp = setTimeout(() => {
        if (!gesture || gesture.moved || touches.size !== 1) return;
        const d = app.view.toDoc(t.sx, t.sy);
        app.run('eyedropper.sampleAt', d.x, d.y);
        gesture.used = true;
      }, 550);
    }
  }

  function updateGesture() {
    const g = gesture;
    if (!g) return;
    for (const t of touches.values()) if (dist(t.sx, t.sy, t.x0, t.y0) > 12) g.moved = true;
    if (g.moved) clearTimeout(g.lp);
    if (touches.size !== g.count) rebase();
    const c = centroid();
    let M = new DOMMatrix().translate(c.x, c.y);
    if (touches.size >= 2) {
      const { d, ang } = twoInfo();
      let rot = 0;
      let da = ang - g.a0;
      if (da > 180) da -= 360; if (da < -180) da += 360;
      if (app.settings.touchRotate) {
        if (!g.rotOn && Math.abs(da) > 12) { g.rotOn = true; g.a0 += Math.sign(da) * 12; da -= Math.sign(da) * 12; }
        if (g.rotOn) rot = da;
      }
      const s = d / Math.max(1, g.d0);
      M = M.rotate(rot).scale(s);
    }
    M = M.translate(-g.c0.x, -g.c0.y);
    const next = M.multiply(g.m0);
    const z = Math.hypot(next.c, next.d);
    if (z > 0.02 && z < 64) app.view.set(next);
  }

  function endGesture() {
    const g = gesture;
    gesture = null;
    if (!g) return;
    clearTimeout(g.lp);
    const dt = performance.now() - g.t0;
    if (!g.moved && !g.used && dt < 320) {
      if (g.max === 2 && app.settings.twoFingerUndo) app.run('edit.undo');
      else if (g.max === 3 && app.settings.threeFingerRedo) app.run('edit.redo');
      else if (g.max === 4) app.run('view.toggleUI');
    }
    if (g.rotOn) {
      // 0/90/180/270도 근처면 맞춰 준다
      const a = app.view.angle;
      const snap = Math.round(a / 90) * 90;
      if (Math.abs(a - snap) < 4) app.view.rotateAt(snap - a);
    }
  }

  // ---- 포인터 이벤트 --------------------------------------------------
  function onDown(e) {
    if (e.target !== stage && e.target.id !== 'view') return;
    const now = performance.now();
    if (e.pointerType === 'touch') {
      if (penIsDown || now - lastPen < app.settings.palmGuardMs) return; // 손바닥 무시
      capture(e.pointerId);
      const p = local(e);
      touches.set(e.pointerId, { sx: p.sx, sy: p.sy, x0: p.sx, y0: p.sy });
      if (active && active.touch) {
        // 손가락으로 그리던 중 두 번째 손가락 → 그리기 취소 후 제스처
        callTool('cancel');
        active = null;
        startGesture();
        return;
      }
      if (gesture) { gesture.max = Math.max(gesture.max, touches.size); rebase(); return; }
      if (app.settings.fingerDraw && touches.size === 1 && !active) {
        const te = trackHover(e);
        active = { id: e.pointerId, mode: 'tool', touch: true };
        callTool('down', te);
        return;
      }
      startGesture();
      return;
    }

    if (e.pointerType === 'pen') { lastPen = now; penIsDown = true; penStats = { n: 0, vals: new Set() }; if (gesture) { touches.clear(); endGesture(); } }
    if (active) return;
    capture(e.pointerId);
    const te = trackHover(e);
    e.preventDefault();

    // 화면 조작 단축
    if (e.pointerType === 'mouse' && e.button === 1) { active = { id: e.pointerId, ...viewDrag('pan', te) }; return; }
    if (keys.space) {
      const mode = keys.ctrl ? 'zoom' : keys.shift ? 'rotate' : 'pan';
      active = { id: e.pointerId, ...viewDrag(mode, te) };
      return;
    }
    if (app.toolId === 'view') { active = { id: e.pointerId, ...viewDrag(app.tool.mode || 'pan', te) }; return; }

    let restore = null;
    const barrel = e.pointerType === 'pen' && (e.button === 2 || (e.buttons & 2));
    const barrel2 = e.pointerType === 'pen' && (e.button === 1 || (e.buttons & 4));      // 두 번째 측면 버튼 (와콤·휴이온 등)
    const eraserEnd = e.pointerType === 'pen' && (e.button === 5 || (e.buttons & 32));    // 펜 뒤쪽 지우개 (와콤·서피스 등)
    if (eraserEnd) restore = beginTemp('eraser');
    else if (barrel || barrel2) {
      const act = barrel2 && !barrel ? (app.settings.penButton2 || 'pan') : app.settings.penButton;
      if (act === 'radial') { openRadial(e.clientX, e.clientY, e.pointerId); return; }
      if (act === 'pan') { active = { id: e.pointerId, ...viewDrag('pan', te) }; return; }
      if (act === 'eyedropper') restore = beginTemp('eyedropper');
      else if (act === 'eraser') restore = beginTemp('eraser');
      else return;
    } else if (e.pointerType === 'mouse' && e.button === 2) {
      if (app.settings.mouseRadial !== false) openRadial(e.clientX, e.clientY, e.pointerId);
      return;
    } else if (keys.alt && app.tool?.allowAltPick !== false) {
      restore = beginTemp('eyedropper');
    }
    active = { id: e.pointerId, mode: 'tool', restore };
    callTool('down', te);
  }

  function onMove(e) {
    if (e.pointerType === 'touch') {
      const t = touches.get(e.pointerId);
      if (t) {
        const p = local(e);
        t.sx = p.sx; t.sy = p.sy;
        if (gesture) updateGesture();
      }
      if (active && active.touch && active.id === e.pointerId) {
        const list = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
        for (const ce of (list.length ? list : [e])) callTool('move', toolEvent(ce));
        trackHover(e);
      }
      return;
    }
    if (e.pointerType === 'pen') { lastPen = performance.now(); if (penStats && e.buttons && penStats.n < 400) { penStats.n++; penStats.vals.add(e.pressure); } }
    const te = trackHover(e);
    if (active && active.id === e.pointerId) {
      if (active.mode === 'tool') {
        if (e.pointerType === 'pen' && app.settings.penFeel?.predict !== false && e.getPredictedEvents) {
          const pr = e.getPredictedEvents();
          app.predicted = pr.length ? pr.map(p => local(p)) : null;
        }
        const list = e.getCoalescedEvents ? e.getCoalescedEvents() : [];
        if (list.length > 1) for (const ce of list) callTool('move', toolEvent(ce));
        else callTool('move', te);
      } else applyViewDrag(active, te);
    } else if (!active) {
      callTool('hover', te);
      if (app.tool?.overlay) app.requestRender();
    }
  }

  function onUp(e) {
    if (e.pointerType === 'touch') {
      if (active && active.touch && active.id === e.pointerId) {
        callTool('up', toolEvent(e));
        active = null;
      }
      if (touches.delete(e.pointerId)) {
        if (!touches.size) endGesture();
        else rebase();
      }
      return;
    }
    if (e.pointerType === 'pen') { lastPen = performance.now(); penIsDown = false; checkPenPressure(); }
    if (!active || active.id !== e.pointerId) return;
    const a = active;
    active = null;
    if (a.mode === 'tool') {
      if (e.type === 'pointercancel') callTool('cancel');
      else callTool('up', toolEvent(e));
      if (a.restore) app.setTool(a.restore);
    }
  }

  stage.addEventListener('pointerdown', onDown);
  stage.addEventListener('pointermove', onMove);
  stage.addEventListener('pointerup', onUp);
  stage.addEventListener('pointercancel', onUp);
  stage.addEventListener('pointerleave', (e) => {
    if (e.pointerType !== 'touch') { app.pointer.inside = false; app.requestRender(); }
  });
  stage.addEventListener('contextmenu', (e) => e.preventDefault());
  for (const ev of ['gesturestart', 'gesturechange', 'gestureend']) document.addEventListener(ev, (e) => e.preventDefault(), { passive: false });
  stage.addEventListener('selectstart', (e) => e.preventDefault());

  stage.addEventListener('wheel', (e) => {
    e.preventDefault();
    const { sx, sy } = local(e);
    if (e.ctrlKey) {
      app.view.zoomAt(Math.exp(-e.deltaY * 0.01), sx, sy);         // 터치패드 핀치
    } else if (e.shiftKey) {
      app.view.panBy(-e.deltaY, 0);
    } else if (app.settings.wheelMode === 'scroll') {
      app.view.panBy(-e.deltaX, -e.deltaY);
    } else if (e.altKey) {
      app.view.rotateAt(e.deltaY > 0 ? 15 : -15);
    } else {
      app.view.zoomAt(e.deltaY < 0 ? 1.15 : 1 / 1.15, sx, sy);
    }
  }, { passive: false });

  // 페이지 확대/스크롤/선택 방지
  document.addEventListener('gesturestart', (e) => e.preventDefault());
  window.addEventListener('wheel', (e) => { if (e.ctrlKey) e.preventDefault(); }, { passive: false });
  document.addEventListener('selectstart', (e) => {
    if (!(e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement)) e.preventDefault();
  });

  return { get busy() { return !!active || !!gesture; } };
}
