// The persona hologram: a head built out of flowing particles, projected up out
// of the loading spinner, with its mouth synced to the voice.
//
// Ported from SyntaxEngine (platform/web/studio/hologram.js). What changed here
// is only WHERE the projection comes from. SYNTAX runs a horizontal canal from
// its main button to the panel edge and fires a cone out of that one aperture.
// This panel is a small floating window, so instead the particles stream off the
// ENTIRE RIM of the spinner — every dot leaves the ring at the point closest to
// where it is going — and converge upward into the head. Nothing about the lips
// changed; see the guard block below.

import { HoloHead } from './holo_head.js';
import { makePalette } from './palette.js';

const TAU = Math.PI * 2;

// ── LIP SIZE — DO NOT RE-TUNE BY HAND ───────────────────────────────────────
// Carried over verbatim from SYNTAX, where the mouth size kept regressing
// because it was re-derived from scattered magic numbers on every "shrink the
// mouth" fix, with no single source of truth. These five constants are the ONE
// source of truth for lip SIZE. Changing any of them changes how the mouth
// reads at rest, which is the thing nobody has complained about.
const LIP = Object.freeze({
  RESTING_PULL_IN: 0.8,    // pull lip particles toward the mouth centre → shrinks the RESTING mouth
  WIDTH_SPREAD: 0.176,     // viseme corner spread/round — how far the corners travel (EE wide / OO in)
  JAW_TRAVEL: 0.104,       // jaw-open travel — how far the lower lip drops on a fully open mouth
  LIP_THRESHOLD: 0.18,     // |jawW| > this OR lipW > this ⇒ a lip particle (vs a face dot)
  DENSITY_DROP: 0.36,      // fraction of lip dots culled so the mouth reads legible, not a packed blob
});

// ── LIP TIMING, IN SECONDS — NOT IN FRAMES ──────────────────────────────────
// Also verbatim from SYNTAX, and the reason the mouth stays on the voice on a
// slow machine. Every filter here used to ease by a fixed fraction PER FRAME.
// A per-frame coefficient is a time constant only if the frame rate is constant,
// so the mouth fell further behind the audio the slower the device got — SYNTAX
// measured 40 ms at 60 fps but 105 ms at 12 fps, and ~90 ms is plainly out of
// sync. Expressed as time constants it is ~40 ms at 60 fps and ~95 ms at 12 fps,
// which is at the floor of what is possible: a mouth cannot update more often
// than the page draws. Do NOT "fix" a slow-machine lag by shortening these — that
// re-tunes how the mouth feels at full frame rate. Make the frame cheaper instead.
//
// Each tau is derived from the old 60 fps coefficient, so at 60 fps the behaviour
// is unchanged to four decimals:
//     approach:  tau = -(1/60) / ln(1 - alpha)      decay: tau = -(1/60) / ln(k)
const LIP_TAU = Object.freeze({
  MOUTH_TRACK:  0.0172,   // was mouth      += (target - mouth) * 0.62
  ENV_ATTACK:   0.0192,   // was _lipEnv    += 0.58 * (raw - _lipEnv)   on a rise
  ENV_RELEASE:  0.0240,   // was _lipEnv    += 0.50 * (raw - _lipEnv)   on a fall
  PULSE_DECAY:  0.0956,   // was mouthOpen  *= 0.84
  TARGET_DECAY: 0.0507,   // was mouthTarget*= 0.72
  LEVEL_DECAY:  0.1304,   // was level      *= 0.88
  VISEME_EASE:  0.1304,   // was vWide      += (target - vWide) * 0.12
  VISEME_REST:  0.1582,   // was _vWideT    *= 0.9
});

/** Per-frame approach fraction for a time constant — the frame-rate-independent form of `x * k`. */
const ease = (dt, tau) => 1 - Math.exp(-dt / tau);
/** Per-frame decay multiplier for a time constant. */
const decay = (dt, tau) => Math.exp(-dt / tau);

export class Hologram {
  constructor(canvas) {
    this.c = canvas;
    this.ctx = canvas.getContext('2d');
    this.t = 0;
    this.state = 'idle';        // idle | thinking | speaking
    this.energy = 0;            // eased 0..1 — overall presence
    this.rise = 0;              // eased 0..1 — how far the projection has risen out of the spinner
    this.head = 0;              // eased 0..1 — head resolve
    this.level = 0;             // speech envelope
    this.mouth = 0;             // eased mouth-open for lip-sync
    this.mouthTarget = 0;
    this._lastMouthAt = -9;     // this.t when setMouth last ran — hold amplitude until the audio goes quiet
    this.flicker = 1;
    this.talking = false;
    this.mouthOpen = 0;         // lip-open amount, pulsed per spoken word
    this.vWide = 0; this._vWideT = 0; this._vi = 0; this._prevLvl = 0; this._lastViseme = -9;
    this._lipEnv = 0;           // slow-attack lip-open envelope — culls quick/jitter words
    this._gzY = 0; this._gzP = 0; this._gzTY = 0; this._gzTP = 0; this._nextGaze = 0;
    this._gzVY = 0; this._gzVP = 0;   // gaze velocities (a damped spring → smooth ease-in/out, no lurch)
    this.ring = null;           // the Prismatic border/spinner — shared phase, energy and geometry
    this.palette = makePalette('prismatic');
    this.reduced = !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
    this._lingering = false;    // after speech, hold the head open for one idle look before folding
    this.lingerUntil = 0;
    this._raf = null;
    this._loop = this._loop.bind(this);
  }

  setPalette(colour) { this.palette = makePalette(colour); this._kick(); }

  start() { this._kick(); }
  relayout() { this._kick(); }
  _kick() { if (!this._raf) this._raf = requestAnimationFrame(this._loop); }

  // Drives the open/close of the projection. When speech ends we do not cut the
  // head instantly — that read as abrupt — it stays projected and glances around
  // briefly, then folds away. The linger expires in _loop.
  setState(s) {
    if (s === 'speaking') { this._lingering = false; this.state = 'speaking'; this._kick(); return; }
    if (s === 'idle' && this.state === 'speaking' && !this.reduced && !this._lingering) {
      this._lingering = true;
      this.lingerUntil = this.t + 1.5;
      this._nextGaze = 0;                // trigger a fresh idle glance immediately
      this._kick();
      return;                            // stay open through the linger
    }
    this._lingering = false;
    this.state = s;
    this._kick();
  }

  pulse(strength = 1) { this.level = Math.min(1.5, this.level + 0.30 * strength); this._kick(); }

  /** A spoken word — the mouth opens. */
  mouthHit(v = 0.9) { this.mouthOpen = Math.min(1, this.mouthOpen + v); this._kick(); }

  /** The amplitude of the audio being heard right now, 0..1. */
  setMouth(v) {
    this.mouthTarget = Math.max(0, Math.min(1, v));
    this._lastMouthAt = this.t;
    if (v > 0.05) this.level = Math.min(1.5, this.level + 0.12);
    this._kick();
  }

  /** Picks the next mouth SHAPE (corner spread / round). */
  _advanceViseme() {
    if (this.t - this._lastViseme < 0.18) return;     // refractory: a calmer mouth than one flip per frame
    this._lastViseme = this.t;
    const VIS = [0.55, -0.45, 0.2, -0.3, 0.4, -0.15];  // +wide (EE) … −round (OO); gentle, not cartoonish
    this._vi = (this._vi + 1) % VIS.length;
    this._vWideT = VIS[this._vi];
  }

  _size() {
    const dpr = Math.min(1.25, window.devicePixelRatio || 1);   // small decorative canvas — cap the fill cost
    const r = this.c.getBoundingClientRect();
    const w = Math.max(2, r.width | 0), h = Math.max(2, r.height | 0);
    if (this.c.width !== w * dpr || this.c.height !== h * dpr) { this.c.width = w * dpr; this.c.height = h * dpr; }
    return { w, h, dpr };
  }

  _loop() {
    // FRAME CAP while not speaking: the ambient thinking shimmer and the
    // fold-away do not need 60fps. Speaking stays full rate so the lips read
    // smoothly, and so does any open/close transition — the transition easing is
    // tuned for 60fps and capping it made the fold run at half speed.
    const _t = (performance && performance.now) ? performance.now() : 0;
    const transitioning = this._lingering || this.rise > 0.02 || this.head > 0.02;
    if (this.state !== 'speaking' && !this.talking && !transitioning
        && this._lastDraw && _t - this._lastDraw < 55) { this._raf = requestAnimationFrame(this._loop); return; }
    this._lastDraw = _t;

    const { w, h, dpr } = this._size();
    const ctx = this.ctx;

    // Measured, clamped delta time → smooth under variable fps, and a long stall
    // cannot lurch the gaze.
    const now = (performance && performance.now) ? performance.now() : this.t * 1000;
    let dt = this._lastNow ? (now - this._lastNow) / 1000 : 0.016; this._lastNow = now;
    // The lip filters get a much larger ceiling than the gaze clamp. 50 ms is
    // 20 fps and a slow machine dips under that; clamping there would cap the
    // very correction that keeps the mouth on the voice. These are stable
    // exponentials, so a long frame can only snap the mouth to the current
    // amplitude — which is the right answer when frames are that far apart.
    const dtLip = Math.max(0, Math.min(dt, 0.25));
    dt = Math.max(0, Math.min(dt, 0.05));
    if (!this.reduced) this.t += dt;

    if (this._lingering && this.t >= this.lingerUntil) { this._lingering = false; this.state = 'idle'; }

    const speaking = this.state === 'speaking';
    this.energy += ((speaking ? 1 : (this.state === 'thinking' ? 0.22 : 0)) - this.energy) * 0.08;

    if (speaking) {
      // The projection rises out of the spinner, then the head builds up.
      this.rise += (1 - this.rise) * 0.16;
      this.head += ((this.rise > 0.55 ? 1 : 0) - this.head) * 0.16;
    } else {
      // Closing is the opening run in reverse: the head dissolves first, then
      // the projection sinks back into the spinner.
      this.head += (0 - this.head) * 0.20;
      if (this.head < 0.12) this.rise += (0 - this.rise) * 0.22;
    }

    // ALL LIP EASING IS TIME-BASED (LIP_TAU) — never a fixed fraction per frame.
    this.level *= decay(dtLip, LIP_TAU.LEVEL_DECAY);
    this.mouthOpen *= decay(dtLip, LIP_TAU.PULSE_DECAY);
    // Hold the last measured amplitude. Decaying the target every frame collapses
    // the jaw between ticks and desyncs the mouth; only decay once the audio has
    // been quiet for ~40 ms, then track tightly.
    if (this.t - this._lastMouthAt > 0.04) this.mouthTarget *= decay(dtLip, LIP_TAU.TARGET_DECAY);
    this.mouth += (this.mouthTarget - this.mouth) * ease(dtLip, LIP_TAU.MOUTH_TRACK);

    // Lip drive from the real amplitude, gated just above the noise floor so the
    // gaps between words close the mouth, then a responsive attack and a smooth
    // release so the jaw actually tracks the speech.
    let lipRaw = Math.max(this.mouth, this.mouthOpen);
    lipRaw = lipRaw > 0.04 ? (lipRaw - 0.04) / 0.96 : 0;
    this._lipEnv += ease(dtLip, lipRaw > this._lipEnv ? LIP_TAU.ENV_ATTACK : LIP_TAU.ENV_RELEASE)
                    * (lipRaw - this._lipEnv);
    // The viseme SHAPE advances on a sustained rise (a new syllable), not on jitter.
    if (this._lipEnv > 0.26 && this._prevLvl <= 0.26) this._advanceViseme();
    this._prevLvl = this._lipEnv;
    this.vWide += (this._vWideT - this.vWide) * ease(dtLip, LIP_TAU.VISEME_EASE);
    if (!speaking) this._vWideT *= decay(dtLip, LIP_TAU.VISEME_REST);

    this._updateGaze(dt);
    this.flicker = this.reduced ? 1 : this.flicker + ((0.86 + Math.random() * 0.14) - this.flicker) * 0.3;

    ctx.save(); ctx.scale(dpr, dpr); ctx.clearRect(0, 0, w, h);
    this._drawProjection(ctx, w, h);
    ctx.restore();

    // Park when there is nothing left to animate — idle costs nothing.
    const idle = !speaking && this.energy < 0.003 && this.rise < 0.003 && this.head < 0.003
                 && !this.talking && !this._lingering && this.level < 0.01 && this.mouthOpen < 0.01;
    if (idle) { this._raf = null; return; }
    this._raf = requestAnimationFrame(this._loop);
  }

  /**
   * Conversational gaze: glance at a point, hold it, return to rest — discrete
   * looks with holds, not a continuous floating drift.
   */
  _updateGaze(dt) {
    if (this.talking || this._lingering) {
      if (this.t > this._nextGaze) {
        // A random spot within a 30° cone, reached directly; the spring below
        // smooths the whole path. Occasionally near forward, as a natural reset.
        const mag = (Math.random() < 0.18 ? Math.random() * 4 : 7 + Math.random() * 23) * Math.PI / 180;
        const dir = Math.random() * TAU;
        this._gzTY = mag * Math.cos(dir);
        this._gzTP = mag * Math.sin(dir) * 0.7;      // gentler vertically than horizontally
        // While lingering the head is scanning before it shuts down — quick,
        // repeated glances; while speaking, long calm holds.
        this._nextGaze = this.t + (this._lingering ? 1.7 + Math.random() * 1.1 : 3.5 + Math.random() * 4.0);
      }
    } else { this._gzTY = 0; this._gzTP = 0; }

    // Barely-there micro-drift so a held gaze is not frozen.
    const wy = 0.010 * Math.sin(this.t * 0.40 + 0.5) + 0.006 * Math.sin(this.t * 0.23 + 2.0);
    const wp = 0.007 * Math.sin(this.t * 0.33 + 1.1);
    const tgtY = this._gzTY + wy, tgtP = this._gzTP + wp;

    // A critically damped spring eases in and out, so no turn starts with a jerk.
    const gk = this._lingering ? 0.02 : 0.003, gd = this._lingering ? 0.82 : 0.88;
    const f = dt * 60;   // dt-scaled so the 60fps tuning survives a variable frame rate
    this._gzVY += (tgtY - this._gzY) * gk * f; this._gzVY *= Math.pow(gd, f); this._gzY += this._gzVY * f;
    this._gzVP += (tgtP - this._gzP) * gk * f; this._gzVP *= Math.pow(gd, f); this._gzP += this._gzVP * f;
  }

  /** Where the spinner is, in canvas pixels — the projection's source. */
  _emitter(w, h) {
    const r = this.ring && this.ring.spinner;
    if (r) return r;
    const radius = Math.max(14, Math.min(w, h) * 0.085);
    return { cx: w / 2, cy: h - radius - 14, r: radius };
  }

  _drawProjection(ctx, w, h) {
    if (this.rise < 0.01 && this.head < 0.01) return;

    const emitter = this._emitter(w, h);
    const phase = this.ring ? this.ring.phase : (this.t * 0.012) % 1;

    // SYNTAX's bottom-up layout, verbatim: the head sits at two fifths of the
    // panel's height so the beam out of the spinner stays long enough to read.
    const scale = Math.min(h * 0.6, w * 0.62, 188);
    const headX = emitter.cx;
    const headY = h * 0.40;

    this._drawGlow(ctx, emitter, headY, scale, phase);

    const head = this._head3d();
    if (!head || !head.ok || !head.loaded || this.head <= 0.01) return;

    const targets = head.targets;
    const mouthCx = head.mouthN ? head.mouthN[0] : 0;
    const mouthCy = head.mouthN ? head.mouthN[1] : 0.12;

    if (!this._pool || this._pool.length !== targets.length) this._pool = targets.map((p) => makeParticle(p));

    const openV = this.rise;
    const resolve = Math.min(1, this.head);
    // Straight from the lip envelope. No pow(): that crushed the dynamic range so
    // a quiet syllable and a loud one opened the mouth almost the same width.
    const mouthOpen = Math.min(1, this._lipEnv * 1.25);
    const yaw = this._gzY, pitch = this._gzP;
    const cosY = Math.cos(yaw), sinY = Math.sin(yaw), cosP = Math.cos(pitch), sinP = Math.sin(pitch);
    const step = this.reduced ? 0 : 0.016;

    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    for (let k = 0; k < this._pool.length; k++) {
      const p = this._pool[k];
      if (p.off) continue;                       // a thinned-out lip dot
      p.t += step;

      if (p.t >= p.life) { recycle(p); continue; }
      if (p.t < 0) continue;                     // a brief gap before its replacement flies in

      const target = targets[k];
      const has3d = target.length >= 5;
      const vW = has3d ? target[3] : target[2];
      const hW = (has3d ? target[4] : target[3]) || 0;

      // Pull the lip particles toward the mouth centre so the whole mouth is
      // smaller — the spread and travel multipliers only shrink the OPEN
      // animation, not the mouth at rest.
      const isLip = Math.abs(vW) > LIP.LIP_THRESHOLD || hW > LIP.LIP_THRESHOLD;
      const bx = isLip ? mouthCx + (target[0] - mouthCx) * LIP.RESTING_PULL_IN : target[0];
      const by = isLip ? mouthCy + (target[1] - mouthCy) * LIP.RESTING_PULL_IN : target[1];

      let rx = bx;
      if (hW > 0) rx += (bx - mouthCx) * this.vWide * hW * LIP.WIDTH_SPREAD;   // corners spread wide / round in
      const ry = by + vW * mouthOpen * LIP.JAW_TRAVEL;                         // the jaw opens

      // True baked depth, so the nose leads and the ears trail through a turn.
      const zf = has3d ? target[2]
        : Math.sqrt(Math.max(0, 1 - (rx * rx) / 0.25 - (ry * ry) / 0.34)) * 0.34;

      const tx = headX + (rx * cosY + zf * sinY) * scale;
      const ty = headY + (ry * cosP + zf * sinP) * scale * openV;

      // THE EMITTER CHANGE: each dot leaves the spinner at the point on its rim
      // nearest its destination, so the stream pours off the whole ring instead
      // of out of one hole. The jitter keeps the rim from looking stippled.
      const angle = Math.atan2(ty - emitter.cy, tx - emitter.cx) + (p.spread - 0.5) * 0.5;
      const ox = emitter.cx + Math.cos(angle) * emitter.r;
      const oy = emitter.cy + Math.sin(angle) * emitter.r;

      let x, y, a;
      if (p.t < p.fly) {
        const e = p.t / p.fly, eased = e * e * (3 - 2 * e);
        x = ox + (tx - ox) * eased; y = oy + (ty - oy) * eased; a = Math.min(1, e * 1.6);
      } else {
        x = tx; y = ty; a = 1 - (p.t - p.fly) / (p.life - p.fly);   // settle → fade → replaced
      }

      ctx.globalAlpha = a * resolve * 0.52;      // bright enough for the face to read
      // Colour by position across the face, scrolling with the ring so the head
      // and the border share one moving spectrum.
      ctx.fillStyle = this.palette.at(((x - emitter.cx) / Math.max(1, scale)) * 0.5 - phase, 0.42, 0.86);
      ctx.beginPath(); ctx.arc(x, y, 1.15, 0, TAU); ctx.fill();
    }
    ctx.restore();
  }

  /**
   * The light the projection casts: a soft column standing on the spinner's rim
   * and widening toward the head. It replaces SYNTAX's cone, which fired from a
   * single aperture; this one is symmetric because the whole ring is emitting.
   */
  _drawGlow(ctx, emitter, headY, scale, phase) {
    const top = headY - scale * 0.5;
    const height = emitter.cy - top;
    if (height < 4) return;

    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.filter = `blur(${Math.max(3, (emitter.r * 0.5) | 0)}px)`;

    const halfTop = scale * 0.55 * this.rise;
    const gradient = ctx.createLinearGradient(0, emitter.cy, 0, top);
    gradient.addColorStop(0.0, this.palette.at(phase, 0.62, 0.86, 0.34 * this.flicker * this.rise));
    gradient.addColorStop(0.45, this.palette.at(phase + 0.2, 0.55, 0.82, 0.16 * this.flicker * this.rise));
    gradient.addColorStop(1.0, this.palette.at(phase + 0.4, 0.55, 0.82, 0));
    ctx.fillStyle = gradient;

    ctx.beginPath();
    ctx.moveTo(emitter.cx - emitter.r, emitter.cy);
    ctx.lineTo(emitter.cx - halfTop, top);
    ctx.lineTo(emitter.cx + halfTop, top);
    ctx.lineTo(emitter.cx + emitter.r, emitter.cy);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  /** Lazily loads the head geometry that supplies the particles' target spots. */
  _head3d() {
    if (this._h3 === undefined) { try { this._h3 = new HoloHead(); } catch { this._h3 = null; } }
    return this._h3;
  }
}

/**
 * Lip particles must HOLD at their jaw-driven spot rather than constantly
 * flying in and fading, or the mouth's motion is lost in the churn — that is
 * why the lip-sync failed to read in SYNTAX even with a correct envelope. They
 * get a short fly-in and a long life so they spend most of their cycle parked on
 * the lips, where the per-frame jaw offset is visible. Face dots keep the churn.
 */
function makeParticle(target) {
  const has3d = target.length >= 5;
  const vW = has3d ? target[3] : target[2];
  const hW = (has3d ? target[4] : target[3]) || 0;
  const lip = Math.abs(vW) > LIP.LIP_THRESHOLD || hW > LIP.LIP_THRESHOLD;

  const p = { spread: Math.random(), lip, off: lip && Math.random() < LIP.DENSITY_DROP };
  recycle(p, true);
  return p;
}

function recycle(p, first = false) {
  if (p.lip) {
    p.t = -Math.random() * (first ? 0.6 : 0.2);
    p.fly = 0.12 + Math.random() * 0.08;
    p.life = 3.2 + Math.random() * 1.6;
  } else {
    p.t = -Math.random() * (first ? 2.2 : 0.5);
    p.fly = 0.4 + Math.random() * 0.35;
    p.life = 1.3 + Math.random() * 1.4;
  }
}
