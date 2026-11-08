// AI 계산 전용 작업자 (화면이 멈추지 않게 따로 돈다). WebGPU 가 되면 그래픽으로, 아니면 CPU 로.
let ortP = null;
const ort = () => (ortP ||= import(new URL('../../vendor/ai/ort/ort.all.min.mjs', import.meta.url).href).then((o) => {
  o.env.wasm.wasmPaths = new URL(new URL('../../vendor/ai/ort/', import.meta.url).href, self.location.href).href;
  o.env.wasm.numThreads = 1;
  o.env.logLevel = 'error';
  return o;
}));

const sessions = new Map();     // key → { s, ep }
const MAX_OPEN = 2;
const bufs = new Map();         // key → 모델 바이트 (CPU 로 다시 열 때 다시 받지 않게, 마지막 하나만)

async function open(key, url, prefer) {
  const had = sessions.get(key);
  if (had && (!prefer || had.ep === prefer)) { sessions.delete(key); sessions.set(key, had); return had; }
  if (had) { sessions.delete(key); try { had.s.release(); } catch { /* 무시 */ } }
  const o = await ort();
  let buf = bufs.get(key);
  if (!buf) {
    const r = await fetch(url);
    if (!r.ok) throw new Error('모델 파일이 없습니다. AI > AI 모델 관리에서 받으세요');
    buf = new Uint8Array(await r.arrayBuffer());
    bufs.clear(); bufs.set(key, buf);
  }
  const eps = prefer === 'wasm' || !self.navigator.gpu || cpuOnly ? ['wasm'] : ['webgpu', 'wasm'];
  let s = null, ep = null, err = null;
  for (const p of eps) {
    // WebGPU 는 NCHW 배치로 (기본 NHWC 는 일부 그래픽에서 계산이 틀어짐)
    const opt = p === 'webgpu' ? { name: 'webgpu', preferredLayout: 'NCHW' } : p;
    try { s = await o.InferenceSession.create(buf, { executionProviders: [opt], graphOptimizationLevel: 'all' }); ep = p; break; } catch (x) { err = x; if (p === 'webgpu') gpuErr = 'open: ' + String(x?.message || x).slice(0, 300); }
  }
  if (!s) throw new Error('모델을 열지 못했습니다: ' + (err?.message || err));
  while (sessions.size >= MAX_OPEN) { const [k, v] = sessions.entries().next().value; sessions.delete(k); try { v.s.release(); } catch { /* 무시 */ } }
  const e = { s, ep };
  sessions.set(key, e);
  return e;
}

let cpuOnly = false, gpuErr = null;
self.onmessage = async ({ data: m }) => {
  const reply = (o, transfer = []) => self.postMessage({ id: m.id, ...o }, transfer);
  try {
    if (m.op === 'config') { cpuOnly = !!m.cpuOnly; return reply({ ok: true }); }
    if (m.op === 'close') { for (const v of sessions.values()) try { v.s.release(); } catch { /* 무시 */ } sessions.clear(); bufs.clear(); return reply({ ok: true }); }
    if (m.op === 'open') { const e = await open(m.key, m.url, m.prefer); return reply({ ok: true, ep: e.ep, inputNames: e.s.inputNames, outputNames: e.s.outputNames }); }
    if (m.op === 'run') {
      const o = await ort();
      const feeds = () => Object.fromEntries(Object.entries(m.feeds).map(([k, t]) => [k, new o.Tensor(t.type, t.data, t.dims)]));
      let e = await open(m.key, m.url, m.prefer), out;
      try { out = await e.s.run(feeds()); } catch (x) {
        if (e.ep !== 'webgpu') throw x;
        gpuErr = 'run: ' + String(x?.message || x).slice(0, 300);
        e = await open(m.key, m.url, 'wasm');                       // 그래픽에서 실패하면 CPU 로 한 번 더
        out = await e.s.run(feeds());
      }
      const res = {}, transfer = [];
      for (const [k, v] of Object.entries(out)) {
        const data = v.data.slice();                                // 작업자 → 화면 쪽으로 넘기기
        res[k] = { type: v.type, data, dims: v.dims };
        transfer.push(data.buffer);
        v.dispose?.();
      }
      return reply({ ok: true, out: res, ep: e.ep, gpuErr }, transfer);
    }
    reply({ ok: false, error: 'unknown op' });
  } catch (x) { reply({ ok: false, error: String(x?.message || x) }); }
};
