// 아주 작은 ZIP 만들기 (압축 없이 저장, 또는 deflate). EPUB·CBZ 용.
const CRC = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; }
  return t;
})();
export function crc32(u8) {
  let c = 0xffffffff;
  for (let i = 0; i < u8.length; i++) c = CRC[(c ^ u8[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

async function deflateRaw(u8) {
  if (typeof CompressionStream === 'undefined') return null;
  try {
    const cs = new CompressionStream('deflate-raw');
    const out = new Response(new Blob([u8]).stream().pipeThrough(cs));
    return new Uint8Array(await out.arrayBuffer());
  } catch { return null; }
}

const enc = new TextEncoder();
const toBytes = async (data) => (typeof data === 'string' ? enc.encode(data) : data instanceof Uint8Array ? data : new Uint8Array(await (data instanceof Blob ? data.arrayBuffer() : data)));

/**
 * files: [{ name, data(string|Blob|Uint8Array|ArrayBuffer), store?: true }]
 * store: 압축하지 않음 (EPUB 의 mimetype, 이미 압축된 그림)
 */
export async function makeZip(files) {
  const parts = [], central = [];
  let offset = 0;
  const dosTime = (() => { const d = new Date(); return { t: (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1), d: ((d.getFullYear() - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate() }; })();
  for (const f of files) {
    const raw = await toBytes(f.data);
    const name = enc.encode(f.name);
    const crc = crc32(raw);
    let body = raw, method = 0;
    if (!f.store && raw.length > 64) { const z = await deflateRaw(raw); if (z && z.length < raw.length) { body = z; method = 8; } }
    const lh = new DataView(new ArrayBuffer(30));
    lh.setUint32(0, 0x04034b50, true); lh.setUint16(4, 20, true); lh.setUint16(6, 0x0800, true); lh.setUint16(8, method, true);
    lh.setUint16(10, dosTime.t, true); lh.setUint16(12, dosTime.d, true); lh.setUint32(14, crc, true);
    lh.setUint32(18, body.length, true); lh.setUint32(22, raw.length, true); lh.setUint16(26, name.length, true); lh.setUint16(28, 0, true);
    parts.push(new Uint8Array(lh.buffer), name, body);
    const ch = new DataView(new ArrayBuffer(46));
    ch.setUint32(0, 0x02014b50, true); ch.setUint16(4, 20, true); ch.setUint16(6, 20, true); ch.setUint16(8, 0x0800, true); ch.setUint16(10, method, true);
    ch.setUint16(12, dosTime.t, true); ch.setUint16(14, dosTime.d, true); ch.setUint32(16, crc, true); ch.setUint32(20, body.length, true); ch.setUint32(24, raw.length, true);
    ch.setUint16(28, name.length, true); ch.setUint32(42, offset, true);
    central.push(new Uint8Array(ch.buffer), name);
    offset += 30 + name.length + body.length;
  }
  const cdSize = central.reduce((a, b) => a + b.length, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true); end.setUint16(8, files.length, true); end.setUint16(10, files.length, true);
  end.setUint32(12, cdSize, true); end.setUint32(16, offset, true);
  return new Blob([...parts, ...central, new Uint8Array(end.buffer)], { type: 'application/zip' });
}

/** 시험·확인용: ZIP 안의 파일 이름과 압축 방식 목록 */
export function listZip(u8) {
  const dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength), out = [];
  let p = 0;
  while (p + 30 <= u8.length && dv.getUint32(p, true) === 0x04034b50) {
    const method = dv.getUint16(p + 8, true), size = dv.getUint32(p + 18, true), nl = dv.getUint16(p + 26, true), el = dv.getUint16(p + 28, true);
    const name = new TextDecoder().decode(u8.subarray(p + 30, p + 30 + nl));
    out.push({ name, method, size, at: p, data: u8.subarray(p + 30 + nl + el, p + 30 + nl + el + size) });
    p += 30 + nl + el + size;
  }
  return out;
}
