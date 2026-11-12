// 네이티브 펜 입력 (ComicPad.exe 전용 창에서만): 펜이 그림판 위에 오면 창 프로그램에 알려서
// 그림판 영역만 거의 투명한 창으로 덮게 하고, Windows Ink 펜 입력(모든 점·1픽셀 이하 위치·필압·기울기·버튼)을
// 창 프로그램에서 받아 여기서 보통의 포인터 이벤트처럼 그림판에 넣는다.
// 펜이 떠나거나 마우스·손가락을 쓰면 창 프로그램이 덮개를 치우고, 그때부터는 웹 화면이 직접 받는다 (지금과 같음).
import { app } from '../app.js';

export const nativePen = { available: false, on: false };

export function initNativePen(stage) {
  const wv = window.chrome?.webview;
  if (!wv || !window.comicpadNative?.pen) return;
  nativePen.available = true;
  const view = document.getElementById('view');
  let down = false, last = null, offset = null, pid = 0;
  const enabled = () => app.settings.nativePen !== false;
  // Wintab: 태블릿 드라이버가 있고 설정에서 골랐을 때만 (Windows Ink 를 꺼 둔 와콤·휴이온·XP-Pen 용)
  const wintab = () => enabled() && app.settings.penApi === 'wintab' && !!window.comicpadNative?.wintab;
  const send = (s) => { try { wv.postMessage(s); } catch { /* 창 프로그램 없음 */ } };
  const r2s = (r) => [r.left, r.top, r.width, r.height].map(v => (+v).toFixed(1)).join(',');
  const isCanvas = (el) => el === stage || el === view;

  // 그림판 위에 떠 있는 버튼·글상자 등은 덮지 않음 (그 자리는 웹이 직접 받음)
  function exclusions(sr) {
    const out = [], seen = new Set();
    const step = 28;
    for (let y = sr.top + 4; y < sr.bottom; y += step) {
      for (let x = sr.left + 4; x < sr.right; x += step) {
        let el = document.elementFromPoint(x, y);
        if (!el || isCanvas(el)) continue;
        // 화면을 통째로 덮는 것(창·메뉴 배경)이면 덮개를 아예 쓰지 않음
        while (el.parentElement && el.parentElement !== document.body && el.parentElement.id !== 'stage-overlay' && el.parentElement !== stage) el = el.parentElement;
        if (seen.has(el)) continue;
        seen.add(el);
        const r = el.getBoundingClientRect();
        if (r.width * r.height > sr.width * sr.height * 0.6) return null;
        out.push(r);
      }
    }
    return out;
  }
  function geometry() {
    const sr = stage.getBoundingClientRect();
    const ex = document.querySelector('.modal-backdrop') ? null : exclusions(sr);
    if (!ex) return null;
    return `${r2s(sr)}|${ex.map(r2s).join(';')}`;
  }
  function enable() {
    if (nativePen.on || !enabled() || down || wintab()) return;
    const g = geometry();
    if (!g) return;
    nativePen.on = true;
    send(`pen:on|${g}`);
  }
  function disable() {
    if (!nativePen.on) return;
    nativePen.on = false;
    send('pen:off');
  }
  let geoT = 0, wtOn = false;
  const refresh = () => {
    clearTimeout(geoT);
    geoT = setTimeout(() => {
      if (wintab()) {
        // Wintab 은 창 프로그램이 펜 위치를 보고 덮개를 켜므로 그림판 영역을 늘 알려 둠 (창이 덮여 있으면 영역 0)
        const g = geometry() || '0,0,0,0|';
        send(`pen:wintab|1|${g}`); wtOn = true;
        return;
      }
      if (wtOn) { send('pen:wintab|0'); wtOn = false; }
      if (!nativePen.on) return;
      const g = geometry();
      if (!g) disable(); else send(`pen:rect|${g}`);
    }, 120);
  };
  new ResizeObserver(refresh).observe(stage);
  new MutationObserver(refresh).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden'] });
  window.addEventListener('resize', refresh);

  // 웹 화면에 펜이 들어오면(그림판 위) 덮개를 켜 달라고
  const onWebPen = (e) => { if (e.isTrusted && e.pointerType === 'pen' && !e.buttons && isCanvas(e.target)) enable(); };
  stage.addEventListener('pointermove', onWebPen, true);
  stage.addEventListener('pointerover', onWebPen, true);

  // ---- 창 프로그램에서 온 펜 입력 → 포인터 이벤트 -------------------------------
  const toEv = (s) => {
    const [x, y, p, tx, ty, tw, buttons, t] = s;
    // 창 프로그램 시각(ms) → 이 페이지 시각: 가장 늦지 않게 도착한 것을 기준으로
    const now = performance.now();
    if (offset == null || now - t < offset) offset = now - t;
    const time = t + offset;
    return { clientX: x, clientY: y, pressure: p, tiltX: tx, tiltY: ty, twist: tw, buttons, pointerType: 'pen', pointerId: pid, timeStamp: time, cpTime: time, isPrimary: true };
  };
  const fire = (type, o, extra = {}) => {
    const ev = new PointerEvent(type, { ...o, bubbles: true, cancelable: true, composed: true, ...extra });
    ev.cpTime = o.cpTime;
    if (extra.cpCoalesced) ev.cpCoalesced = extra.cpCoalesced;
    (type === 'pointerleave' ? stage : view).dispatchEvent(ev);
  };
  const endStroke = () => { if (down && last) fire('pointerup', { ...last, buttons: 0, pressure: 0 }, { button: 0 }); down = false; };

  wv.addEventListener('message', (msg) => {
    let m;
    try { m = typeof msg.data === 'string' ? JSON.parse(msg.data) : msg.data; } catch { return; }
    if (!m || m.t !== 'pen') return;
    if (m.k === 'wintab-fail') {
      app.settings.penApi = 'ink'; app.saveSettingsSoon();
      app.toast('Wintab 을 켜지 못해 Windows Ink 로 받습니다 (태블릿 드라이버를 확인하세요)', 'warn');
      return;
    }
    if (m.k === 'hidden' || m.k === 'leave') {
      endStroke();
      nativePen.on = false;
      if (last) fire('pointerleave', last);
      return;
    }
    if (!m.e?.length) return;
    pid = 2000 + (m.id || 0);
    const evs = m.e.map(toEv);
    const lastEv = evs[evs.length - 1];
    if (m.k === 'down') {
      // 그림판이 아닌 곳(떠 있는 버튼 등)에 대면: 덮개를 치우고 이번 입력은 넘김
      if (!isCanvas(document.elementFromPoint(lastEv.clientX, lastEv.clientY))) { if (!wintab()) disable(); return; }
      if (evs.length > 1) fire('pointermove', evs[evs.length - 2], { cpCoalesced: evs.slice(0, -1) });
      down = true; last = lastEv;
      fire('pointerdown', lastEv, { button: m.b >= 0 ? m.b : 0 });
    } else if (m.k === 'up') {
      if (evs.length > 1) fire('pointermove', evs[evs.length - 2], { cpCoalesced: evs.slice(0, -1) });
      last = lastEv;
      if (down) fire('pointerup', { ...lastEv, buttons: 0, pressure: 0 }, { button: m.b >= 0 ? m.b : 0 });
      down = false;
    } else {
      if (!down && !isCanvas(document.elementFromPoint(lastEv.clientX, lastEv.clientY))) { if (!wintab()) disable(); return; }
      last = lastEv;
      fire('pointermove', lastEv, { cpCoalesced: evs });
    }
  });

  // 설정에서 끄면 바로 치우기
  app.bus.on('settings:changed', () => { if (!enabled() || wintab()) disable(); refresh(); });
  refresh();
  app.nativePen = { enable, disable, state: nativePen };
}

/** 커서 그림을 창 프로그램에도 (덮개 위에서도 같은 펜촉·십자가 보이게) */
export async function sendNativeCursor(css) {
  const wv = window.chrome?.webview;
  if (!wv || !window.comicpadNative?.pen) return;
  const m = /url\("([^"]+)"\)\s*([\d.]+)\s+([\d.]+)/.exec(css || '');
  if (!m) {
    const kw = (css || 'default').split(',').pop().trim();
    wv.postMessage(`pen:cursor|kw|${kw}`);
    return;
  }
  try {
    const img = new Image();
    img.src = m[1];
    await img.decode();
    const k = Math.max(1, window.devicePixelRatio || 1);
    const c = document.createElement('canvas');
    c.width = Math.round(img.naturalWidth * k); c.height = Math.round(img.naturalHeight * k);
    c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
    const b64 = c.toDataURL('image/png').split(',')[1];
    wv.postMessage(`pen:cursor|png|${(+m[2] * k).toFixed(0)},${(+m[3] * k).toFixed(0)}|${b64}`);
  } catch { /* 그림을 못 만들면 그대로 */ }
}
