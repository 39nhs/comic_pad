// 공용 유틸리티

export const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
export const lerp = (a, b, t) => a + (b - a) * t;
export const dist = (ax, ay, bx, by) => Math.hypot(bx - ax, by - ay);

let _uid = 0;
export const uid = (p = 'id') => `${p}${Date.now().toString(36)}${(_uid++).toString(36)}`;

export function createCanvas(w, h) {
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(w));
  c.height = Math.max(1, Math.round(h));
  return c;
}

export function ctx2d(canvas, opts) {
  return canvas.getContext('2d', opts);
}

// ---- 사각형 -------------------------------------------------------------
export function rect(x, y, w, h) { return { x, y, w, h }; }

export function rectUnion(a, b) {
  if (!a) return b ? { ...b } : null;
  if (!b) return { ...a };
  const x = Math.min(a.x, b.x), y = Math.min(a.y, b.y);
  return { x, y, w: Math.max(a.x + a.w, b.x + b.w) - x, h: Math.max(a.y + a.h, b.y + b.h) - y };
}

export function rectClamp(r, w, h) {
  if (!r) return null;
  const x0 = clamp(Math.floor(r.x), 0, w), y0 = clamp(Math.floor(r.y), 0, h);
  const x1 = clamp(Math.ceil(r.x + r.w), 0, w), y1 = clamp(Math.ceil(r.y + r.h), 0, h);
  if (x1 <= x0 || y1 <= y0) return null;
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

export function rectFromPoints(ax, ay, bx, by) {
  return { x: Math.min(ax, bx), y: Math.min(ay, by), w: Math.abs(bx - ax), h: Math.abs(by - ay) };
}

export function rectPad(r, p) { return { x: r.x - p, y: r.y - p, w: r.w + p * 2, h: r.h + p * 2 }; }

export function pointsBounds(pts) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const p of pts) {
    if (p.x < x0) x0 = p.x; if (p.x > x1) x1 = p.x;
    if (p.y < y0) y0 = p.y; if (p.y > y1) y1 = p.y;
  }
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

// ---- 색 -----------------------------------------------------------------
export function hexToRgb(hex) {
  let h = hex.replace('#', '');
  if (h.length === 3) h = h.split('').map(c => c + c).join('');
  const n = parseInt(h.slice(0, 6), 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

export function rgbToHex(r, g, b) {
  return '#' + [r, g, b].map(v => clamp(Math.round(v), 0, 255).toString(16).padStart(2, '0')).join('');
}

export function hsvToRgb(h, s, v) {
  const f = (n) => { const k = (n + h / 60) % 6; return v - v * s * Math.max(0, Math.min(k, 4 - k, 1)); };
  return { r: f(5) * 255, g: f(3) * 255, b: f(1) * 255 };
}

export function rgbToHsv(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  if (d) {
    if (max === r) h = ((g - b) / d) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
    h *= 60; if (h < 0) h += 360;
  }
  return { h, s: max ? d / max : 0, v: max };
}

// ---- 비동기 ---------------------------------------------------------------
export const sleep = (ms) => new Promise(r => setTimeout(r, ms));

export function debounce(fn, ms) {
  let t = 0;
  const d = (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
  d.flush = (...a) => { clearTimeout(t); fn(...a); };
  return d;
}

export function throttleRAF(fn) {
  let queued = false, args = null;
  return (...a) => {
    args = a;
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => { queued = false; fn(...args); });
  };
}

export function canvasToBlob(canvas, type = 'image/png', quality) {
  return new Promise((res, rej) => canvas.toBlob(b => (b ? res(b) : rej(new Error('toBlob 실패'))), type, quality));
}

export function blobToDataURL(blob) {
  return new Promise((res, rej) => {
    const fr = new FileReader();
    fr.onload = () => res(fr.result);
    fr.onerror = rej;
    fr.readAsDataURL(blob);
  });
}

export function loadImage(src) {
  return new Promise((res, rej) => {
    const img = new Image();
    img.onload = () => res(img);
    img.onerror = () => rej(new Error('이미지를 불러오지 못했습니다: ' + String(src).slice(0, 60)));
    img.src = src;
  });
}

export async function blobToImage(blob) {
  const url = URL.createObjectURL(blob);
  try { return await loadImage(url); } finally { setTimeout(() => URL.revokeObjectURL(url), 1000); }
}

export function downloadBlob(blob, filename) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}

export function pickFile(accept) {
  return new Promise((res) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = accept;
    input.onchange = () => res(input.files[0] || null);
    input.click();
  });
}

// 아이콘·짧은 꾸밈 글자만 허용하는 HTML 넣기 (공유 받은 프리셋·파일 이름 등에 섞인 스크립트 차단)
const SAFE_TAGS = new Set(['svg', 'path', 'circle', 'rect', 'line', 'polyline', 'polygon', 'ellipse', 'g', 'span', 'b', 'i', 'em', 'strong', 'small', 'br', 'kbd', 'sub', 'sup', 'u', 's']);
const BAD_ATTR = /^(on|href$|xlink:href$|src$|srcset$|action$|formaction$)/i;
export function sanitizeInto(root, allowTag) {
  const walk = (node) => {
    for (const c of [...node.childNodes]) {
      if (c.nodeType === 1) {
        if (!allowTag(c.localName)) { c.replaceWith(document.createTextNode(c.textContent)); continue; }
        for (const a of [...c.attributes]) {
          if (BAD_ATTR.test(a.name) || (a.name === 'style' && /url\s*\(|expression/i.test(a.value))) c.removeAttribute(a.name);
        }
        walk(c);
      } else if (c.nodeType !== 3) c.remove();
    }
  };
  walk(root);
  return root;
}
export const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
export function setHTML(el, html) {
  const t = document.createElement('template');   // template 안은 실행·로딩이 일어나지 않음
  t.innerHTML = String(html ?? '');
  sanitizeInto(t.content, (n) => SAFE_TAGS.has(n));
  el.replaceChildren(t.content);
  return el;
}

// DOM 헬퍼: h('div.cls', {attrs}, children...)
export function h(tag, attrs, ...children) {
  const [name, ...classes] = tag.split('.');
  const el = document.createElement(name || 'div');
  if (classes.length) el.className = classes.join(' ');
  if (attrs) {
    for (const [k, v] of Object.entries(attrs)) {
      if (v == null || v === false) continue;
      if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
      else if (k === 'html') setHTML(el, v);
      else if (k in el && typeof v !== 'string') el[k] = v;
      else el.setAttribute(k, v === true ? '' : v);
    }
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

export function fileStamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

export function safeName(s) {
  return String(s).replace(/[\\/:*?"<>|]+/g, '_').trim() || '제목없음';
}
