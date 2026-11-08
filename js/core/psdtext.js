// PSD 에 글자 레이어를 "글자 그대로" 넣기 (포토샵에서 열면 텍스트 도구로 고칠 수 있게)
// TySh 블록 = 위치 행렬 + 글자 설명자(Txt, EngineData) + 왜곡 설명자 + 상자
const te = new TextEncoder();

class Buf {
  constructor() { this.a = []; this.n = 0; }
  push(b) { this.a.push(b); this.n += b.length; }
  u8(v) { this.push(new Uint8Array([v & 255])); }
  u16(v) { const b = new Uint8Array(2); new DataView(b.buffer).setUint16(0, v); this.push(b); }
  u32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, v); this.push(b); }
  i32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setInt32(0, v); this.push(b); }
  f64(v) { const b = new Uint8Array(8); new DataView(b.buffer).setFloat64(0, v); this.push(b); }
  ascii(s) { this.push(new Uint8Array([...s].map(c => c.charCodeAt(0) & 255))); }
  bytes() { const o = new Uint8Array(this.n); let p = 0; for (const b of this.a) { o.set(b, p); p += b.length; } return o; }
}

// ---- 포토샵 설명자 쓰기 ----
const uni = (b, s) => { b.u32(s.length + 1); for (const c of s) b.u16(c.charCodeAt(0)); b.u16(0); };
const key = (b, k) => { if (k.length === 4) { b.u32(0); b.ascii(k); } else { b.u32(k.length); b.ascii(k); } };
function value(b, v) {
  if (v.t === 'TEXT') { b.ascii('TEXT'); uni(b, v.v); }
  else if (v.t === 'enum') { b.ascii('enum'); key(b, v.type); key(b, v.v); }
  else if (v.t === 'long') { b.ascii('long'); b.i32(v.v); }
  else if (v.t === 'doub') { b.ascii('doub'); b.f64(v.v); }
  else if (v.t === 'bool') { b.ascii('bool'); b.u8(v.v ? 1 : 0); }
  else if (v.t === 'tdta') { b.ascii('tdta'); b.u32(v.v.length); b.push(v.v); }
  else if (v.t === 'UntF') { b.ascii('UntF'); b.ascii(v.unit); b.f64(v.v); }
  else if (v.t === 'Objc') { b.ascii('Objc'); descriptor(b, v.cls, v.items); }
  else if (v.t === 'VlLs') { b.ascii('VlLs'); b.u32(v.v.length); for (const x of v.v) value(b, x); }
}
export function descriptor(b, cls, items) {
  uni(b, '');
  key(b, cls);
  b.u32(items.length);
  for (const [k, v] of items) { key(b, k); value(b, v); }
}

// ---- EngineData (포토샵 글자 엔진 설명: PostScript 비슷한 문법) ----
/** "þÿ" + UTF-16BE, 괄호·역슬래시는 바이트마다 이스케이프 */
function psString(s) {
  const out = [0x28, 0xfe, 0xff];
  for (const c of s) {
    const u = c.charCodeAt(0);
    for (const byte of [u >> 8, u & 255]) { if (byte === 0x28 || byte === 0x29 || byte === 0x5c) out.push(0x5c); out.push(byte); }
  }
  out.push(0x29);
  return new Uint8Array(out);
}

// 윈도 글꼴 이름 → 포스트스크립트 이름 (포토샵이 찾는 이름)
const PS_FONTS = {
  'Malgun Gothic': ['MalgunGothic', 'MalgunGothicBold'], '맑은 고딕': ['MalgunGothic', 'MalgunGothicBold'],
  NanumGothic: ['NanumGothic', 'NanumGothicBold'], NanumMyeongjo: ['NanumMyeongjo', 'NanumMyeongjoBold'], NanumBarunGothic: ['NanumBarunGothic', 'NanumBarunGothicBold'], NanumPen: ['NanumPen', 'NanumPen'],
  Gulim: ['Gulim', 'Gulim'], Dotum: ['Dotum', 'Dotum'], Batang: ['Batang', 'Batang'], Gungsuh: ['Gungsuh', 'Gungsuh'],
  Arial: ['ArialMT', 'Arial-BoldMT'], 'Comic Sans MS': ['ComicSansMS', 'ComicSansMS-Bold'], Impact: ['Impact', 'Impact'],
  'Yu Gothic': ['YuGothic-Regular', 'YuGothic-Bold'], 'MS Gothic': ['MS-Gothic', 'MS-Gothic'],
};
export const psFontName = (font, bold) => { const f = PS_FONTS[font]; return f ? f[bold ? 1 : 0] : String(font || 'MalgunGothic').replace(/\s+/g, '') + (bold ? '-Bold' : ''); };

function engineData(m, text) {
  const n = text.length + 1;   // 끝의 \r 포함
  const hex = m.color.replace('#', '');
  const rgb = [0, 2, 4].map(i => (parseInt(hex.slice(i, i + 2), 16) / 255).toFixed(5));
  const just = { left: 0, right: 1, center: 2 }[m.align] ?? 0;
  const lead = (m.size * (m.lineHeight || 1.2)).toFixed(3);
  const track = Math.round((m.tracking || 0) * 10);
  const font = psFontName(m.font, m.bold);
  const style = `/Font 0 /FontSize ${m.size.toFixed(3)} /FauxBold ${m.bold && !PS_FONTS[m.font] ? 'true' : 'false'} /FauxItalic ${m.italic ? 'true' : 'false'} /AutoLeading false /Leading ${lead} /Tracking ${track} /FillColor << /Type 1 /Values [ 1.00000 ${rgb.join(' ')} ] >> /FillFlag true /StrokeFlag ${m.strokeWidth > 0 ? 'true' : 'false'}`;
  const para = `/Justification ${just} /FirstLineIndent 0.0 /StartIndent 0.0 /EndIndent 0.0 /SpaceBefore 0.0 /SpaceAfter 0.0 /AutoHyphenate false`;
  const fontSet = '/FontSet [ << /Name ' + '\u0001' + ' /Script 0 /FontType 1 /Synthetic 0 >> << /Name ' + '\u0002' + ' /Script 0 /FontType 0 /Synthetic 0 >> ]';
  const res = `<< /KinsokuSet [ ] /MojiKumiSet [ ] /TheNormalStyleSheet 0 /TheNormalParagraphSheet 0 /ParagraphSheetSet [ << /Name \u0003 /DefaultStyleSheet 0 /Properties << ${para} >> >> ] /StyleSheetSet [ << /Name \u0003 /StyleSheetData << ${style} >> >> ] ${fontSet} /SuperscriptSize .583 /SuperscriptPosition .333 /SubscriptSize .583 /SubscriptPosition .333 /SmallCapSize .7 >>`;
  const body = `

<<
	/EngineDict
	<<
		/Editor << /Text \u0000 >>
		/ParagraphRun << /DefaultRunData << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << >> >> /Adjustments << /Axis [ 1.0 0.0 1.0 ] /XY [ 0.0 0.0 ] >> >> /RunArray [ << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << ${para} >> >> /Adjustments << /Axis [ 1.0 0.0 1.0 ] /XY [ 0.0 0.0 ] >> >> ] /RunLengthArray [ ${n} ] /IsJoinable 1 >>
		/StyleRun << /DefaultRunData << /StyleSheet << /StyleSheetData << >> >> >> /RunArray [ << /StyleSheet << /StyleSheetData << ${style} >> >> >> ] /RunLengthArray [ ${n} ] /IsJoinable 2 >>
		/GridInfo << /GridIsOn false /ShowGrid false /GridSize 18.0 /GridLeading 22.0 /GridColor << /Type 1 /Values [ 0.0 0.0 0.0 1.0 ] >> /GridLeadingFillColor << /Type 1 /Values [ 0.0 0.0 0.0 1.0 ] >> /AlignLineHeightToGridFlags false >>
		/AntiAlias 4
		/UseFractionalGlyphWidths true
		/Rendered << /Version 1 /Shapes << /WritingDirection ${m.vertical ? 2 : 0} /Children [ << /ShapeType 0 /Procession 0 /Lines << /WritingDirection ${m.vertical ? 2 : 0} /Children [ ] >> /Cookie << /Photoshop << /ShapeType 0 /PointBase [ 0.0 0.0 ] /Base << /ShapeType 0 /TransformPoint0 [ 1.0 0.0 ] /TransformPoint1 [ 0.0 1.0 ] /TransformPoint2 [ 0.0 0.0 ] >> >> >> >> ] >> >>
	>>
	/ResourceDict ${res}
	/DocumentResources ${res}
>>`;
  // 자리표시(\u0000 글자, \u0001·\u0002 글꼴 이름)를 UTF-16 문자열로 바꿔 넣는다
  const parts = [];
  let last = 0;
  for (let i = 0; i < body.length; i++) {
    const ch = body[i];
    if (ch === '\u0000' || ch === '\u0001' || ch === '\u0002' || ch === '\u0003') {
      parts.push(te.encode(body.slice(last, i)));
      parts.push(psString(ch === '\u0000' ? text + '\r' : ch === '\u0001' ? font : ch === '\u0002' ? 'AdobeInvisFont' : 'Normal RGB'));
      last = i + 1;
    }
  }
  parts.push(te.encode(body.slice(last)));
  const total = parts.reduce((a, p) => a + p.length, 0), out = new Uint8Array(total);
  let p = 0;
  for (const x of parts) { out.set(x, p); p += x.length; }
  return out;
}

export { Buf };

/** '8BIM' + 키 + 길이 + (설명자 버전 16 + 설명자) — 길이는 짝수로 맞춤 */
export function descriptorBlock(blockKey, cls, items, prefix = null) {
  const d = new Buf();
  if (prefix) prefix(d);
  d.u32(16);
  descriptor(d, cls, items);
  return keyedBlock(blockKey, d.bytes());
}

/** '8BIM' + 키 + 길이(짝수로 채움) + 데이터 */
export function keyedBlock(blockKey, data) {
  const out = new Buf();
  out.ascii('8BIM'); out.ascii(blockKey);
  const pad = data.length % 2;
  out.u32(data.length + pad);
  out.push(data);
  if (pad) out.u8(0);
  return out.bytes();
}

/** 글자 레이어 meta → TySh 블록 바이트 ('8BIM' 'TySh' 길이 포함) */
export function tyshBlock(m, bounds) {
  const plain = String(m.text || '').replace(/\{([^{}|]+)\|[^{}]+\}/g, '$1');   // 루비 표시는 빼고 본문만
  const text = plain.replace(/\r?\n/g, '\r');
  // 위치: 포토샵 점 글자의 기준점 = 첫 줄 기준선 (가운데 정렬이면 가운데)
  const ox = m.x, oy = m.y + m.size * 0.82;
  let t = [1, 0, 0, 1, ox, oy];
  if (m.mat) { const [a, b, c, d, e, f] = m.mat; t = [a, b, c, d, a * ox + c * oy + e, b * ox + d * oy + f]; }
  const d = new Buf();
  d.u16(1);
  for (const v of t) d.f64(v);
  d.u16(50); d.u32(16);
  descriptor(d, 'TxLr', [
    ['Txt ', { t: 'TEXT', v: text }],
    ['textGridding', { t: 'enum', type: 'textGridding', v: 'None' }],
    ['Ornt', { t: 'enum', type: 'Ornt', v: m.vertical ? 'Vrtc' : 'Hrzn' }],
    ['AntA', { t: 'enum', type: 'Annt', v: 'antiAliasSharp' }],
    ['TextIndex', { t: 'long', v: 0 }],
    ['EngineData', { t: 'tdta', v: engineData(m, text) }],
  ]);
  d.u16(1); d.u32(16);
  descriptor(d, 'warp', [
    ['warpStyle', { t: 'enum', type: 'warpStyle', v: 'warpNone' }],
    ['warpValue', { t: 'doub', v: 0 }],
    ['warpPerspective', { t: 'doub', v: 0 }],
    ['warpPerspectiveOther', { t: 'doub', v: 0 }],
    ['warpRotate', { t: 'enum', type: 'Ornt', v: 'Hrzn' }],
  ]);
  const b = bounds || { x: 0, y: 0, w: 0, h: 0 };
  d.i32(Math.round(b.x - ox)); d.i32(Math.round(b.y - oy)); d.i32(Math.round(b.x + b.w - ox)); d.i32(Math.round(b.y + b.h - oy));
  const data = d.bytes();
  const out = new Buf();
  out.ascii('8BIM'); out.ascii('TySh');
  const pad = data.length % 2;
  out.u32(data.length + pad);
  out.push(data);
  if (pad) out.u8(0);
  return out.bytes();
}
