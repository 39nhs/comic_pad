// 실행취소/다시실행. 픽셀 변경은 변경 영역의 전/후 ImageData 를 저장한다.
import { app } from '../app.js';
import { createCanvas, rectClamp } from './util.js';

export class History {
  constructor() {
    this.undoStack = [];
    this.redoStack = [];
    this.bytes = 0;
    this.maxBytes = 768 * 1024 * 1024;
    this.maxSteps = 120;
    this._batch = null;
    this._snapPool = [];
  }

  get canUndo() { return this.undoStack.length > 0; }
  get canRedo() { return this.redoStack.length > 0; }

  clear() {
    this.undoStack = [];
    this.redoStack = [];
    this.bytes = 0;
    this._snapPool = [];
    this._emit();
  }

  _emit() { app.bus.emit('history:changed', this); }

  /** entry: { label, undo(), redo(), bytes } */
  push(entry) {
    if (this._batch) { this._batch.push(entry); return; }
    this.undoStack.push(entry);
    this.bytes += entry.bytes || 0;
    for (const e of this.redoStack) this.bytes -= e.bytes || 0;
    this.redoStack = [];
    while (this.undoStack.length > 1 && (this.undoStack.length > this.maxSteps || this.bytes > this.maxBytes)) {
      const old = this.undoStack.shift();
      this.bytes -= old.bytes || 0;
    }
    app.markModified();
    this._emit();
  }

  /** fn 안에서 push 되는 항목을 하나로 묶는다 */
  batch(label, fn) {
    if (this._batch) return fn();
    const items = [];
    this._batch = items;
    let result;
    try { result = fn(); } finally { this._batch = null; }
    if (items.length) {
      this.push({
        label,
        bytes: items.reduce((s, e) => s + (e.bytes || 0), 0),
        undo() { for (let i = items.length - 1; i >= 0; i--) items[i].undo(); },
        redo() { for (const e of items) e.redo(); },
      });
    }
    return result;
  }

  undo() {
    if (app.tool?.busy) return;
    const e = this.undoStack.pop();
    if (!e) return;
    e.undo();
    this.redoStack.push(e);
    app.markModified();
    this._emit();
    app.requestRender();
  }

  redo() {
    if (app.tool?.busy) return;
    const e = this.redoStack.pop();
    if (!e) return;
    e.redo();
    this.undoStack.push(e);
    app.markModified();
    this._emit();
    app.requestRender();
  }

  _acquireSnap(w, h) {
    let c = this._snapPool.pop();
    if (!c || c.width !== w || c.height !== h) c = createCanvas(w, h);
    return c;
  }

  /**
   * 레이어 픽셀 변경 시작. 반환된 토큰의 commit(rect, label) 로 기록.
   * 변경 전 레이어를 GPU 상에서 통째로 복사해 두었다가 commit 시 rect 만 읽는다.
   */
  beginPixels(layer) {
    const snap = this._acquireSnap(layer.canvas.width, layer.canvas.height);
    const sctx = snap.getContext('2d');
    sctx.globalCompositeOperation = 'copy';
    sctx.drawImage(layer.canvas, 0, 0);
    sctx.globalCompositeOperation = 'source-over';
    let done = false;
    return {
      snapshot: snap,
      commit: (r, label = '그리기') => {
        if (done) return;
        done = true;
        const c = rectClamp(r, layer.canvas.width, layer.canvas.height);
        if (c) {
          const before = sctx.getImageData(c.x, c.y, c.w, c.h);
          const after = layer.ctx.getImageData(c.x, c.y, c.w, c.h);
          this.push({
            label,
            bytes: c.w * c.h * 8,
            undo: () => { layer.ctx.putImageData(before, c.x, c.y); layer.touch(); },
            redo: () => { layer.ctx.putImageData(after, c.x, c.y); layer.touch(); },
          });
        }
        this._snapPool.push(snap);
      },
      cancel: () => {
        if (done) return;
        done = true;
        layer.ctx.save();
        layer.ctx.globalCompositeOperation = 'copy';
        layer.ctx.drawImage(snap, 0, 0);
        layer.ctx.restore();
        layer.touch();
        this._snapPool.push(snap);
      },
    };
  }

  /** 간단 헬퍼: fn 이 레이어를 수정. rect 를 모르면 레이어 전체 */
  pixels(layer, label, fn, rect) {
    const t = this.beginPixels(layer);
    const r = fn(layer.ctx);
    t.commit(rect || r || { x: 0, y: 0, w: layer.canvas.width, h: layer.canvas.height }, label);
    layer.touch();
  }
}
