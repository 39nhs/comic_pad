// WebGL2 렌더러: 셀(만화식) 음영 + 법선/깊이 기반 선화 추출
import { M, mannequinParts, mannequinMatrices, handParts, handMatrices } from './engine.js';

const VS = `#version 300 es
in vec3 aPos; in vec3 aNrm; in vec2 aUv;
uniform mat4 uModel, uView, uProj; uniform mat3 uNrm;
out vec3 vN; out vec3 vVN; out vec2 vUv; out float vDepth;
void main(){
  vec4 w = uModel * vec4(aPos,1.0);
  vec4 v = uView * w;
  vN = normalize(uNrm * aNrm);
  vVN = normalize(mat3(uView) * vN);
  vUv = aUv; vDepth = -v.z;
  gl_Position = uProj * v;
}`;

const FS = `#version 300 es
precision highp float;
in vec3 vN; in vec3 vVN; in vec2 vUv; in float vDepth;
uniform int uMode;            // 0 미리보기, 1 색, 2 음영, 3 법선/깊이, 4 바닥 격자
uniform vec3 uColor; uniform sampler2D uTex; uniform bool uHasTex;
uniform vec3 uLight; uniform float uLevels, uSoft, uAmbient, uFar;
uniform vec3 uShadow;
out vec4 o;
float toon(float l){
  if (uLevels < 0.5) return l;
  float x = l * uLevels;
  float f = floor(x), t = fract(x);
  float s = max(uSoft, 0.001);
  return clamp((f + smoothstep(0.5 - s, 0.5 + s, t)) / uLevels, 0.0, 1.0);
}
void main(){
  vec3 N = normalize(vN);
  if (!gl_FrontFacing) N = -N;
  vec3 base = uColor;
  if (uHasTex) base *= texture(uTex, vUv).rgb;
  float lam = max(dot(N, normalize(uLight)), 0.0);
  float lit = toon(clamp(uAmbient + (1.0 - uAmbient) * lam, 0.0, 1.0));
  if (uMode == 0) { o = vec4(mix(base * uShadow, base, lit), 1.0); }
  else if (uMode == 1) { o = vec4(base, 1.0); }
  else if (uMode == 2) { o = vec4(uShadow, 1.0 - lit); }
  else if (uMode == 3) { vec3 vn = normalize(vVN); if (!gl_FrontFacing) vn = -vn; o = vec4(vn * 0.5 + 0.5, max(vDepth / uFar, 0.002)); }
  else { o = vec4(0.5, 0.5, 0.55, 0.5); }
}`;

const QVS = `#version 300 es
out vec2 vUv;
void main(){ vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); vUv = p; gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }`;

const EDGE = `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uGeo; uniform vec2 uTexel; uniform float uWidth, uNThr, uDThr; uniform vec3 uLine;
out vec4 o;
void main(){
  vec4 c = texture(uGeo, vUv);
  float e = 0.0;
  for (int i = 0; i < 8; i++) {
    float a = float(i) * 0.785398;
    vec2 d = vec2(cos(a), sin(a)) * uTexel * uWidth * 0.5;
    vec4 n = texture(uGeo, vUv + d);
    bool cb = c.a <= 0.0, nb = n.a <= 0.0;
    if (cb && nb) continue;
    if (cb != nb) { e = 1.0; break; }
    float dd = abs(c.a - n.a) / max(min(c.a, n.a), 1e-4);
    if (dd > uDThr) e = max(e, 1.0);
    float nd = dot(c.rgb * 2.0 - 1.0, n.rgb * 2.0 - 1.0);
    e = max(e, smoothstep(uNThr + 0.06, uNThr - 0.06, nd));
  }
  o = vec4(uLine, e);
}`;

function compile(gl, vs, fs) {
  const mk = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)); return s; };
  const p = gl.createProgram();
  gl.attachShader(p, mk(gl.VERTEX_SHADER, vs)); gl.attachShader(p, mk(gl.FRAGMENT_SHADER, fs));
  gl.bindAttribLocation(p, 0, 'aPos'); gl.bindAttribLocation(p, 1, 'aNrm'); gl.bindAttribLocation(p, 2, 'aUv');
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
  const u = {};
  const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
  for (let i = 0; i < n; i++) { const info = gl.getActiveUniform(p, i); u[info.name] = gl.getUniformLocation(p, info.name); }
  return { p, u };
}

export class Renderer3D {
  constructor(canvas) {
    this.canvas = canvas;
    const gl = canvas.getContext('webgl2', { antialias: true, alpha: true, premultipliedAlpha: false, preserveDrawingBuffer: true });
    if (!gl) throw new Error('이 기기는 WebGL2 를 지원하지 않아 3D 기능을 쓸 수 없습니다');
    this.gl = gl;
    this.floatOK = !!gl.getExtension('EXT_color_buffer_float');
    this.surf = compile(gl, VS, FS);
    this.edge = compile(gl, QVS, EDGE);
    this.gpu = new WeakMap();        // mesh -> 버퍼
    this.tex = new WeakMap();        // image -> 텍스처
    this.fbo = null;
    this.emptyVao = gl.createVertexArray();
    this.grid = this._gridMesh();
  }

  _gridMesh() {
    const pos = [];
    for (let i = -10; i <= 10; i++) pos.push(i, 0, -10, i, 0, 10, -10, 0, i, 10, 0, i);
    return { pos: new Float32Array(pos), nrm: new Float32Array(pos.length).fill(0), idx: null, lines: true };
  }

  _buffers(mesh) {
    let g = this.gpu.get(mesh);
    if (g) return g;
    const gl = this.gl;
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const buf = (data, loc, n) => { const b = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, b); gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, n, gl.FLOAT, false, 0, 0); };
    buf(mesh.pos, 0, 3); buf(mesh.nrm, 1, 3);
    if (mesh.uv) buf(mesh.uv, 2, 2); else { gl.disableVertexAttribArray(2); gl.vertexAttrib2f(2, 0, 0); }
    let count = mesh.pos.length / 3, indexed = false;
    if (mesh.idx) { const ib = gl.createBuffer(); gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib); gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, mesh.idx, gl.STATIC_DRAW); count = mesh.idx.length; indexed = true; }
    gl.bindVertexArray(null);
    g = { vao, count, indexed };
    this.gpu.set(mesh, g);
    return g;
  }

  _texture(img) {
    let t = this.tex.get(img);
    if (t) return t;
    const gl = this.gl;
    t = gl.createTexture();
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
    this.tex.set(img, t);
    return t;
  }

  _fbo(w, h) {
    const gl = this.gl;
    if (this.fbo && this.fbo.w === w && this.fbo.h === h) return this.fbo;
    if (this.fbo) { gl.deleteFramebuffer(this.fbo.fb); gl.deleteTexture(this.fbo.tex); gl.deleteRenderbuffer(this.fbo.depth); }
    const tex = gl.createTexture();
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    if (this.floatOK) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, w, h, 0, gl.RGBA, gl.HALF_FLOAT, null);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const depth = gl.createRenderbuffer();
    gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, w, h);
    const fb = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    this.fbo = { fb, tex, depth, w, h };
    return this.fbo;
  }

  /** 장면을 그릴 목록으로 펼친다: [{ mesh, model, color, image }] */
  drawList(scene) {
    const out = [];
    for (const n of scene.nodes) {
      if (n.visible === false) continue;
      const base = M.trs(n.pos, n.rot, n.scale);
      if (n.kind === 'hand') {
        const parts = handParts(n.side || 1), mats = handMatrices(n.pose || {}, n.side || 1);
        for (const k of Object.keys(parts)) out.push({ mesh: parts[k], model: M.mul(base, mats[k]), color: n.color, image: null });
      } else if (n.kind === 'mannequin') {
        const parts = mannequinParts(), mats = mannequinMatrices(n.pose || {}, n.body);
        for (const k of Object.keys(parts)) out.push({ mesh: parts[k], model: M.mul(base, mats[k]), color: n.color, image: null });
      } else for (const p of n.parts || []) out.push({ mesh: p.mesh, model: M.mul(base, p.matrix), color: n.tint ? n.color : (p.color || n.color), image: p.image });
    }
    return out;
  }

  camera(cam, aspect) {
    const d = Math.PI / 180;
    const ce = Math.cos(cam.pitch * d), eye = [
      cam.target[0] + cam.dist * ce * Math.sin(cam.yaw * d),
      cam.target[1] + cam.dist * Math.sin(cam.pitch * d),
      cam.target[2] + cam.dist * ce * Math.cos(cam.yaw * d)];
    const view = M.lookAt(eye, cam.target, [0, 1, 0]);
    const near = Math.max(0.01, cam.dist * 0.01), far = cam.dist * 20 + 50;
    // 화각이 작을수록 원근감이 약해짐 (망원)
    const proj = M.persp(cam.fov * d, aspect, near, far);
    return { view, proj, eye, far };
  }

  _drawObjects(list, mode, cam, st, aspect) {
    const gl = this.gl, s = this.surf;
    const { view, proj, far } = this.camera(cam, aspect);
    gl.useProgram(s.p);
    gl.uniformMatrix4fv(s.u.uView, false, view);
    gl.uniformMatrix4fv(s.u.uProj, false, proj);
    gl.uniform1i(s.u.uMode, mode);
    const d = Math.PI / 180;
    gl.uniform3f(s.u.uLight, Math.cos(st.lightEl * d) * Math.sin(st.lightAz * d), Math.sin(st.lightEl * d), Math.cos(st.lightEl * d) * Math.cos(st.lightAz * d));
    gl.uniform1f(s.u.uLevels, st.levels); gl.uniform1f(s.u.uSoft, st.soft); gl.uniform1f(s.u.uAmbient, st.ambient); gl.uniform1f(s.u.uFar, far);
    const sc = hex3(st.shadowColor);
    gl.uniform3f(s.u.uShadow, sc[0], sc[1], sc[2]);
    gl.uniform1i(s.u.uTex, 1);   // 모델 텍스처는 1번 (0번은 선화용 버퍼)
    for (const it of list) {
      const g = this._buffers(it.mesh);
      gl.uniformMatrix4fv(s.u.uModel, false, it.model);
      gl.uniformMatrix3fv(s.u.uNrm, false, M.normal3(it.model));
      const c = Array.isArray(it.color) ? it.color : hex3(it.color || '#d8d8d8');
      gl.uniform3f(s.u.uColor, c[0], c[1], c[2]);
      if (it.image) { gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, this._texture(it.image)); gl.uniform1i(s.u.uHasTex, 1); }
      else gl.uniform1i(s.u.uHasTex, 0);
      gl.bindVertexArray(g.vao);
      if (it.mesh.lines) gl.drawArrays(gl.LINES, 0, g.count);
      else if (g.indexed) gl.drawElements(gl.TRIANGLES, g.count, gl.UNSIGNED_INT, 0);
      else gl.drawArrays(gl.TRIANGLES, 0, g.count);
    }
    gl.bindVertexArray(null);
  }

  /**
   * 장면을 그린다. what: 'preview' | 'color' | 'shade' | 'line'
   * st: 스타일 { levels, soft, ambient, lightAz, lightEl, shadowColor, line, lineWidth, nThr, dThr, lineColor }
   */
  render(scene, w, h, what, st, opt = {}) {
    const gl = this.gl;
    if (this.canvas.width !== w || this.canvas.height !== h) { this.canvas.width = w; this.canvas.height = h; }
    const list = this.drawList(scene);
    const aspect = w / h;
    gl.disable(gl.CULL_FACE);
    gl.enable(gl.DEPTH_TEST);
    const lineScale = opt.lineScale || 1;
    const drawLines = () => {
      const f = this._fbo(w, h);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, null);   // 그리는 대상 텍스처를 읽지 않도록
      gl.bindFramebuffer(gl.FRAMEBUFFER, f.fb);
      gl.viewport(0, 0, w, h);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      gl.disable(gl.BLEND);
      this._drawObjects(list, 3, scene.camera, st, aspect);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, w, h);
      gl.disable(gl.DEPTH_TEST);
      gl.enable(gl.BLEND);
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      const e = this.edge;
      gl.useProgram(e.p);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, f.tex);
      gl.uniform1i(e.u.uGeo, 0);
      gl.uniform2f(e.u.uTexel, 1 / w, 1 / h);
      gl.uniform1f(e.u.uWidth, Math.max(1, st.lineWidth * lineScale));
      gl.uniform1f(e.u.uNThr, st.nThr);
      gl.uniform1f(e.u.uDThr, st.dThr);
      const lc = hex3(st.lineColor);
      gl.uniform3f(e.u.uLine, lc[0], lc[1], lc[2]);
      gl.bindVertexArray(this.emptyVao);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      gl.bindVertexArray(null);
      gl.bindTexture(gl.TEXTURE_2D, null);
      gl.enable(gl.DEPTH_TEST);
    };
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, w, h);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    if (what === 'preview') {
      gl.disable(gl.BLEND);
      if (opt.grid) { this._drawObjects([{ mesh: this.grid, model: M.ident(), color: '#888888' }], 4, scene.camera, st, aspect); }
      this._drawObjects(list, 0, scene.camera, st, aspect);
      if (st.line) drawLines();
    } else if (what === 'color') {
      gl.disable(gl.BLEND);
      this._drawObjects(list, 1, scene.camera, st, aspect);
    } else if (what === 'shade') {
      // 음영: 앞쪽 면만 남도록 깊이를 먼저 채운 뒤 그림자 색을 그린다
      gl.colorMask(false, false, false, false);
      this._drawObjects(list, 1, scene.camera, st, aspect);
      gl.colorMask(true, true, true, true);
      gl.depthFunc(gl.LEQUAL);
      gl.disable(gl.BLEND);
      this._drawObjects(list, 2, scene.camera, st, aspect);
      gl.depthFunc(gl.LESS);
    } else if (what === 'line') {
      drawLines();
    }
    return this.canvas;
  }

  /** 2D 캔버스로 복사 (WebGL 은 아래위가 뒤집혀 있지 않음: drawImage 로 그대로) */
  snapshot(scene, w, h, what, st, opt) {
    this.render(scene, w, h, what, st, opt);
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    c.getContext('2d').drawImage(this.canvas, 0, 0);
    return c;
  }
}

export function hex3(hex) {
  const n = parseInt(String(hex).slice(1), 16);
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}
