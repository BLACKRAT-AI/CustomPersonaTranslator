// CPT hologram renderer.
//   - WebGL fragment shader applies scan-lines, RGB split, vertical flicker
//     and a bottom-up materialize sweep to a persona image texture.
//   - 2D canvas under it renders the audio waveform (bars driven by `level`).
//   - Messages from the WPF host arrive via window.chrome.webview.

const personaCanvas = document.getElementById('personaCanvas');
const waveCanvas = document.getElementById('wave');
const statusEl = document.getElementById('status');
const transcriptEl = document.getElementById('transcript');
const micBtn = document.getElementById('mic');
const holoEl = document.getElementById('holo');
const toggleBtn = document.getElementById('toggleVisual');

let visualHidden = false;
function applyVisualHidden() {
  if (holoEl) holoEl.classList.toggle('hidden', visualHidden);
  document.body.classList.toggle('audioOnly', visualHidden);
  // Legacy in-page toggle is hidden in the new projector layout — only
  // update its text if it actually exists (older builds).
  if (toggleBtn) toggleBtn.textContent = visualHidden ? 'show visual' : 'hide visual';
}
if (toggleBtn) {
  toggleBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    visualHidden = !visualHidden;
    applyVisualHidden();
  });
}

let state = 'idle'; // idle | appearing | speaking | listening | sending
let appearStartTs = 0;
let level = 0;
let transcriptVisible = false;
let personaImageUrl = null; // data URL or path; null = procedural placeholder
let hologramColor = [0.13, 0.83, 0.93]; // cyan
let glitchIntensity = 0.5;

// --- WebGL --------------------------------------------------------------
const gl = personaCanvas.getContext('webgl', { premultipliedAlpha: false, alpha: true });
let program, tex, posBuf, uniforms;

const VERT = `
attribute vec2 a_pos;
varying vec2 v_uv;
void main() { v_uv = a_pos * 0.5 + 0.5; v_uv.y = 1.0 - v_uv.y; gl_Position = vec4(a_pos, 0.0, 1.0); }
`;
const FRAG = `
precision mediump float;
varying vec2 v_uv;
uniform sampler2D u_tex;
uniform float u_time;
uniform float u_appear;       // 0..1, materialize progress
uniform float u_glitch;       // 0..1 intensity
uniform float u_speakLevel;   // 0..1 audio energy
uniform vec3  u_color;        // hologram tint
uniform float u_hasTexture;   // 0 or 1

float rand(vec2 p) { return fract(sin(dot(p, vec2(12.9898,78.233))) * 43758.5453); }

void main() {
  vec2 uv = v_uv;

  // Glitch displacement bands.
  float band = step(0.97, rand(vec2(floor(u_time*5.0), floor(uv.y*40.0))));
  uv.x += (band - 0.5) * 0.04 * u_glitch * (0.6 + u_speakLevel);

  // RGB split.
  float split = 0.004 * (u_glitch * 0.6 + u_speakLevel * 0.4);
  vec4 base;
  if (u_hasTexture > 0.5) {
    float r = texture2D(u_tex, uv + vec2(split, 0.0)).r;
    float g = texture2D(u_tex, uv).g;
    float b = texture2D(u_tex, uv - vec2(split, 0.0)).b;
    float a = texture2D(u_tex, uv).a;
    base = vec4(r, g, b, a);
  } else {
    // Procedural silhouette: vertical gradient + soft ellipse.
    vec2 c = uv - vec2(0.5, 0.55);
    float ellipse = smoothstep(0.45, 0.25, length(c * vec2(1.0, 1.3)));
    base = vec4(vec3(ellipse), ellipse);
  }

  // Tint toward hologram color.
  vec3 tinted = mix(base.rgb, u_color * (0.3 + base.r * 1.4), 0.7);

  // Scan lines.
  float scan = 0.85 + 0.15 * sin(uv.y * 600.0 + u_time * 8.0);
  tinted *= scan;

  // Flicker.
  float flick = 1.0 - 0.08 * rand(vec2(u_time * 23.0, 0.0)) * u_glitch;
  tinted *= flick;

  // Bottom-up materialize sweep.
  float sweepEdge = u_appear * 1.1;
  float visible = step(1.0 - uv.y, sweepEdge);
  // Bright edge band right at the sweep front.
  float edgeBand = exp(-pow(((1.0 - uv.y) - sweepEdge) * 30.0, 2.0));
  tinted += u_color * edgeBand * 0.6 * step(u_appear, 0.999);

  float alpha = base.a * visible * (0.3 + 0.7 * u_appear);

  gl_FragColor = vec4(tinted, alpha);
}
`;

function initGl() {
  if (!gl) return;
  function shader(t, src) {
    const s = gl.createShader(t); gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) console.warn(gl.getShaderInfoLog(s));
    return s;
  }
  program = gl.createProgram();
  gl.attachShader(program, shader(gl.VERTEX_SHADER, VERT));
  gl.attachShader(program, shader(gl.FRAGMENT_SHADER, FRAG));
  gl.linkProgram(program);
  gl.useProgram(program);

  posBuf = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, posBuf);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 1,-1, -1,1, 1,1]), gl.STATIC_DRAW);
  const a_pos = gl.getAttribLocation(program, 'a_pos');
  gl.enableVertexAttribArray(a_pos);
  gl.vertexAttribPointer(a_pos, 2, gl.FLOAT, false, 0, 0);

  tex = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([255,255,255,255]));

  gl.enable(gl.BLEND);
  gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

  uniforms = {
    time: gl.getUniformLocation(program, 'u_time'),
    appear: gl.getUniformLocation(program, 'u_appear'),
    glitch: gl.getUniformLocation(program, 'u_glitch'),
    speakLevel: gl.getUniformLocation(program, 'u_speakLevel'),
    color: gl.getUniformLocation(program, 'u_color'),
    hasTexture: gl.getUniformLocation(program, 'u_hasTexture'),
  };
}

function loadPersonaImage(url) {
  if (!gl) return;
  const img = new Image();
  img.crossOrigin = 'anonymous';
  img.onload = () => {
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
    personaImageUrl = url;
  };
  img.onerror = () => { personaImageUrl = null; };
  img.src = url;
}

function resizeCanvases() {
  const dpr = window.devicePixelRatio || 1;
  for (const c of [personaCanvas, waveCanvas]) {
    if (!c) continue;
    const r = c.getBoundingClientRect();
    c.width = Math.max(1, Math.floor(r.width * dpr));
    c.height = Math.max(1, Math.floor(r.height * dpr));
  }
  if (gl) gl.viewport(0, 0, personaCanvas.width, personaCanvas.height);
}
window.addEventListener('resize', resizeCanvases);

function renderFrame(ts) {
  if (gl) {
    let appear = 1.0;
    if (state === 'appearing') {
      const t = Math.min(1, (ts - appearStartTs) / 500);
      appear = t;
      if (t >= 1) state = 'speaking';
    } else if (state === 'sending') {
      const t = Math.min(1, (ts - appearStartTs) / 400);
      appear = 1.0 - t;
      if (t >= 1) state = 'idle';
    } else if (state === 'idle') {
      appear = 0;
    }

    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.useProgram(program);
    gl.uniform1f(uniforms.time, ts / 1000);
    gl.uniform1f(uniforms.appear, appear);
    const speaking = state === 'speaking' || state === 'listening';
    gl.uniform1f(uniforms.glitch, glitchIntensity * (0.6 + (speaking ? 0.4 * level : 0)));
    gl.uniform1f(uniforms.speakLevel, speaking ? level : 0);
    gl.uniform3f(uniforms.color, ...hologramColor);
    gl.uniform1f(uniforms.hasTexture, personaImageUrl ? 1.0 : 0.0);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }
  drawWaveform();
  requestAnimationFrame(renderFrame);
}

function drawWaveform() {
  if (!waveCanvas) return;
  const ctx = waveCanvas.getContext('2d');
  const w = waveCanvas.width, h = waveCanvas.height;
  ctx.clearRect(0, 0, w, h);
  // Leave space on the right for the mic icon (rendered as a separate overlay).
  const iconReserve = 36 * (window.devicePixelRatio || 1);
  const drawWidth = Math.max(20, w - iconReserve);
  const bars = 40;
  const gap = 2;
  const bw = (drawWidth - gap * (bars - 1)) / bars;
  const cy = h / 2;
  const [r, g, b] = hologramColor;
  ctx.fillStyle = `rgba(${(r*255)|0},${(g*255)|0},${(b*255)|0},0.95)`;
  for (let i = 0; i < bars; i++) {
    const phase = (performance.now() / 250 + i * 0.7);
    const idleAmp = 0.05 + 0.03 * Math.sin(phase);
    const liveAmp = level * (0.45 + 0.55 * Math.abs(Math.sin(phase * 1.4 + i)));
    const amp = state === 'speaking' || state === 'listening' ? liveAmp : idleAmp;
    const bh = Math.max(2, amp * h * 0.82);
    ctx.fillRect(i * (bw + gap), cy - bh / 2, bw, bh);
  }
}

// --- Host bridge --------------------------------------------------------
function setStatus(s) { statusEl.textContent = s; }
function setState(s) {
  state = s;
  if (s === 'appearing' || s === 'sending') appearStartTs = performance.now();
  setStatus(s);
}

function appendTranscript(chunk) {
  if (!transcriptVisible) return;
  transcriptEl.textContent += chunk;
  transcriptEl.scrollTop = transcriptEl.scrollHeight;
}

function setTranscriptVisible(v) {
  transcriptVisible = !!v;
  transcriptEl.classList.toggle('shown', transcriptVisible);
  if (!transcriptVisible) transcriptEl.textContent = '';
}

function applyPersona(p) {
  if (!p) return;
  if (p.image) loadPersonaImage(p.image); else personaImageUrl = null;
  if (p.color) hologramColor = hexToRgb(p.color);
  if (typeof p.glitch === 'number') glitchIntensity = p.glitch;
  setTranscriptVisible(!!p.transcript);
}

function applyAvatarVisible(v) {
  if (!v) personaImageUrl = null;
}

function hexToRgb(hex) {
  const m = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
  if (!m) return [0.13, 0.83, 0.93];
  return [parseInt(m[1],16)/255, parseInt(m[2],16)/255, parseInt(m[3],16)/255];
}

function postToHost(payload) {
  if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
    window.chrome.webview.postMessage(payload);
  }
}

// PTT — only wired if the legacy in-page mic button is still present.
// The new projector-bar UI hides the in-page mic and routes PTT through the
// WPF window instead, so we no-op when micBtn is missing.
let micActive = false;
function micDown() {
  if (micActive) return; micActive = true;
  if (micBtn) micBtn.classList.add('active');
  setState('listening'); postToHost({ type: 'mic_down' });
}
function micUp() {
  if (!micActive) return; micActive = false;
  if (micBtn) micBtn.classList.remove('active');
  setState('speaking'); postToHost({ type: 'mic_up' });
}
if (micBtn) {
  micBtn.addEventListener('mousedown', micDown);
  micBtn.addEventListener('mouseup', micUp);
  micBtn.addEventListener('mouseleave', () => micActive && micUp());
}
window.addEventListener('keydown', e => { if (e.code === 'Space' && !e.repeat) micDown(); });
window.addEventListener('keyup', e => { if (e.code === 'Space') micUp(); });

// Inbound messages from host
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', ev => {
    const m = ev.data || {};
    switch (m.type) {
      case 'persona': applyPersona(m); break;
      case 'appear':  setState('appearing'); break;
      case 'speaking': setState('speaking'); break;
      case 'listening': setState('listening'); break;
      case 'sending': setState('sending'); break;
      case 'hide':    setState('sending'); break;
      case 'level':   level = Math.max(0, Math.min(1, m.level || 0)); break;
      case 'transcript_chunk': appendTranscript(m.text || ''); break;
      case 'transcript_clear': transcriptEl.textContent = ''; break;
      case 'transcript_visible': setTranscriptVisible(!!m.visible); break;
      case 'avatar_visible': applyAvatarVisible(!!m.visible); break;
      case 'visual_state': visualHidden = !!m.hidden; applyVisualHidden(); break;
      case 'toast': showToast(m.text || ''); break;
    }
  });
}

function showToast(text) {
  const t = document.createElement('div');
  t.textContent = text;
  Object.assign(t.style, {
    position: 'fixed', left: '8px', right: '8px', top: '8px',
    background: 'rgba(244,114,182,0.18)', color: '#fde68a',
    border: '1px solid rgba(244,114,182,0.6)', borderRadius: '6px',
    padding: '6px 10px', fontSize: '11px', zIndex: 9999,
    opacity: '0', transition: 'opacity 200ms'
  });
  document.body.appendChild(t);
  requestAnimationFrame(() => t.style.opacity = '1');
  setTimeout(() => { t.style.opacity = '0'; setTimeout(() => t.remove(), 250); }, 6000);
}

initGl();
resizeCanvases();
applyVisualHidden();
requestAnimationFrame(renderFrame);
postToHost({ type: 'ready' });
