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
import { drawProjectionStyle } from './hologram_styles.js';
import { makePalette } from './palette.js';

const TAU = Math.PI * 2;

/** Width of the head cloud in normalised units (measured from the asset: ±0.288). */
const HEAD_WIDTH = 0.576;

/** The head size the particle count is calibrated against. */
const BASE_SCALE = 200;

/** How fast the loudness reference falls, per level update at 60Hz (about 2s). */
const LEVEL_PEAK_DECAY = 0.994;

/** The quietest reference ever used, so silence is not amplified into speech. */
const LEVEL_MIN_PEAK = 0.02;

/** Below this the audio is silence between words, not quiet speech. */
const LEVEL_SILENCE = 0.004;

/** A dot is always this big. Crispness is not a function of panel size. */
const DOT_RADIUS = 1.3;

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
    this._levelPeak = 0;        // loudest audio heard lately; the mouth is measured against it
    this._gzY = 0; this._gzP = 0; this._gzTY = 0; this._gzTP = 0; this._nextGaze = 0;
    this._gzVY = 0; this._gzVP = 0;   // gaze velocities (a damped spring → smooth ease-in/out, no lurch)
    this.ring = null;           // the Prismatic clock — shared phase and energy
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

  /**
   * A word arrived in the transcript. This is a FALLBACK ONLY.
   *
   * Transcript chunks are produced by the language model streaming its answer,
   * which happens seconds before that sentence is synthesised and heard. Letting
   * them open the mouth is what put the lips out of sync with the voice: the
   * head was miming text that had not been spoken yet. So a chunk only moves
   * anything when no real playback level has arrived recently -- i.e. when there
   * is no audio analysis to sync to at all.
   */
  mouthHit(v = 0.9) {
    if (this.t - this._lastMouthAt < 1.0) return;
    this.mouthOpen = Math.min(1, this.mouthOpen + v);
    this._kick();
  }

  /**
   * The amplitude of the audio coming out of the speaker right now, 0..1. This
   * is the lip-sync, and the only thing that should drive the jaw while the
   * voice is audible.
   *
   * The value is NORMALISED against the loudest audio heard recently, because
   * an absolute amplitude is not a mouth position. A cloned voice measured 0.051
   * peak where a preset measured 0.857: the jaw was opening about half a pixel
   * and the head looked completely still. Measuring each engine against its own
   * loudness makes a quiet voice open the mouth as wide as a loud one, which is
   * what a person does.
   */
  setMouth(v) {
    const level = Math.max(0, Math.min(1, Number(v) || 0));
    this._lastMouthAt = this.t;
    this.mouthOpen = 0;                 // a real level supersedes any fallback pulse

    // A slowly decaying peak: it rises instantly with the voice and falls back
    // over a couple of seconds, so one loud syllable cannot deafen the rest of
    // the sentence and a quiet passage is not amplified into a flapping jaw.
    this._levelPeak = Math.max(level, this._levelPeak * LEVEL_PEAK_DECAY);

    // Below the noise floor is silence, not quiet speech. Without this the
    // normalisation would stretch the gap between words into a wide-open mouth.
    this.mouthTarget = level < LEVEL_SILENCE
      ? 0
      : Math.min(1, level / Math.max(this._levelPeak, LEVEL_MIN_PEAK));

    if (this.mouthTarget > 0.05) this.level = Math.min(1.5, this.level + 0.12);
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
  /**
   * The projector's aperture: a small opening at the middle of the panel's
   * bottom edge. The light and every particle come out of THIS, which is what
   * makes it read as a projection rather than as a glowing floor.
   */
  _emitter(w, h) {
    const halfWidth = Math.max(6, w * 0.05);
    return { y: h - 2, cx: w / 2, half: halfWidth };
  }

  _drawProjection(ctx, w, h) {
    if (this.rise < 0.01 && this.head < 0.01) return;

    const emitter = this._emitter(w, h);
    const phase = this.ring ? this.ring.phase : (this.t * 0.012) % 1;

    // Sized so the dark backdrop behind the head (radius 0.8 x scale) fits
    // inside the canvas and fades out before any edge. There is no panel to
    // fill, so the head is sized to leave that halo room rather than to reach
    // the sides -- the window is transparent, so nothing shows there anyway.
    const scale = Math.min(w * 0.60, h * 0.62);
    const headX = emitter.cx;
    // Half the gap it used to leave above the bar: the projection sat marooned
    // in the middle of the window with a long empty beam under it.
    const headY = scale * 0.5 + h * 0.14;

    this._drawAura(ctx, headX, headY, scale);
    this._drawGlow(ctx, emitter, headY, scale, phase);


    const head = this._head3d();
    if (!head || !head.ok || !head.loaded || this.head <= 0.01) return;

    const targets = head.targets;
    const mouthCx = head.mouthN ? head.mouthN[0] : 0;
    const mouthCy = head.mouthN ? head.mouthN[1] : 0.12;

    if (['scanlines', 'steam', 'lasers'].includes(this.style)) {
      const mouthOpen = Math.min(1, this._lipEnv * 1.25);
      const cosY = Math.cos(this._gzY), sinY = Math.sin(this._gzY), cosP = Math.cos(this._gzP), sinP = Math.sin(this._gzP);
      const points = targets.map(target => {
        const has3d = target.length >= 5;
        const jaw = has3d ? target[3] : target[2], lip = (has3d ? target[4] : target[3]) || 0;
        const isLip = Math.abs(jaw) > LIP.LIP_THRESHOLD || lip > LIP.LIP_THRESHOLD;
        const bx = isLip ? mouthCx + (target[0] - mouthCx) * LIP.RESTING_PULL_IN : target[0];
        const by = isLip ? mouthCy + (target[1] - mouthCy) * LIP.RESTING_PULL_IN : target[1];
        const x = bx + (bx - mouthCx) * this.vWide * lip * LIP.WIDTH_SPREAD;
        const y = by + jaw * mouthOpen * LIP.JAW_TRAVEL;
        const z = has3d ? target[2] : Math.sqrt(Math.max(0, 1 - x * x / 0.25 - y * y / 0.34)) * 0.34;
        return {x: headX + (x * cosY + z * sinY) * scale, y: headY + (y * cosP + z * sinP) * scale * this.rise, z};
      });
      drawProjectionStyle(ctx, this.style, points, {t: this.t, scale, phase, palette: this.palette,
        resolve: Math.min(1, this.head), emitter, headX, headY, targets, reduced: this.reduced});
      return;
    }

    // DENSITY, NOT DOT SIZE.
    //
    // A dot is a dot: 1.3 px, always, at every panel size. Scaling the dots
    // with the head is what turned a crisp face into a handful of fat blobs.
    // What scales instead is HOW MANY there are -- the cloud is a sampling of a
    // surface, so a bigger surface gets proportionally more samples and the
    // density on screen, and therefore the crispness, never changes.
    const copies = Math.max(1, Math.min(6, Math.round((scale * scale) / (BASE_SCALE * BASE_SCALE))));
    if (!this._pool || this._copies !== copies || this._pool.length !== targets.length * copies) {
      this._copies = copies;
      this._pool = [];
      for (let c = 0; c < copies; c++)
        for (const target of targets) this._pool.push(makeParticle(target, c));
    }

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

      const target = targets[k % targets.length];
      const has3d = target.length >= 5;
      const vW = has3d ? target[3] : target[2];
      const hW = (has3d ? target[4] : target[3]) || 0;

      // Pull the lip particles toward the mouth centre so the whole mouth is
      // smaller — the spread and travel multipliers only shrink the OPEN
      // animation, not the mouth at rest.
      const isLip = Math.abs(vW) > LIP.LIP_THRESHOLD || hW > LIP.LIP_THRESHOLD;
      const bx = (isLip ? mouthCx + (target[0] - mouthCx) * LIP.RESTING_PULL_IN : target[0]) + p.jx;
      const by = (isLip ? mouthCy + (target[1] - mouthCy) * LIP.RESTING_PULL_IN : target[1]) + p.jy;

      let rx = bx;
      if (hW > 0) rx += (bx - mouthCx) * this.vWide * hW * LIP.WIDTH_SPREAD;   // corners spread wide / round in
      const ry = by + vW * mouthOpen * LIP.JAW_TRAVEL;                         // the jaw opens

      // True baked depth, so the nose leads and the ears trail through a turn.
      const zf = has3d ? target[2]
        : Math.sqrt(Math.max(0, 1 - (rx * rx) / 0.25 - (ry * ry) / 0.34)) * 0.34;

      const tx = headX + (rx * cosY + zf * sinY) * scale;
      const ty = headY + (ry * cosP + zf * sinP) * scale * openV;

      // THE EMITTER: each dot leaves the modal's edge at the point directly
      // below where it is going, so the stream pours off the whole edge rather
      // than out of one hole. The jitter keeps the line from looking stippled.
      // Out of the aperture, and only the aperture -- a projector has one
      // opening. The spread across its narrow width is what fans the beam.
      const ox = emitter.cx + (p.spread - 0.5) * 2 * emitter.half;
      const oy = emitter.y;

      let x, y, a;
      if (p.t < p.fly) {
        const e = p.t / p.fly, eased = e * e * (3 - 2 * e);
        x = ox + (tx - ox) * eased; y = oy + (ty - oy) * eased; a = Math.min(1, e * 1.6);
      } else {
        x = tx; y = ty; a = 1 - (p.t - p.fly) / (p.life - p.fly);   // settle → fade → replaced
      }

      // Brightness. The panel behind the head is dark, so this can sit well
      // above SYNTAX's alpha without the face turning into a white smear.
      ctx.globalAlpha = a * resolve * 0.85;
      // Colour by position across the face, scrolling with the phase so the head
      // and the bar's border share one moving spectrum.
      ctx.fillStyle = this.palette.at(((x - emitter.cx) / Math.max(1, scale)) * 0.5 - phase, 0.72, 0.74);
      ctx.beginPath(); ctx.arc(x, y, DOT_RADIUS, 0, TAU); ctx.fill();

    }
    ctx.restore();
  }

  /**
   * The soft dark radial the head is read against.
   *
   * This is SYNTAX's #holoBack, drawn into the canvas instead of as a CSS
   * backdrop-filter: a dark circle CENTRED ON THE HEAD, masked so it fades to
   * nothing well before any edge. It is the whole reason bright thin dots are
   * legible over a bright desktop, and it is not a panel -- there is nothing
   * behind the projection but the screen.
   */
  _drawAura(ctx, headX, headY, scale) {
    // SYNTAX uses S * 0.8. Clamped so a head sitting low in the canvas cannot
    // push the halo off the top, which would put a hard edge back.
    // Tight to the head. At 0.8 it read as a huge black halo AROUND the head
    // rather than something behind it: the job is contrast under the dots, and
    // contrast only helps where there are dots.
    const radius = Math.min(scale * 0.52, headY * 0.98);
    const depth = Math.min(1, this.head + this.rise * 0.5);
    if (depth < 0.01) return;

    const gradient = ctx.createRadialGradient(headX, headY, radius * 0.05, headX, headY, radius);
    // Darker at the centre and gone sooner, so it reads as the head being lit
    // rather than the panel being dimmed.
    gradient.addColorStop(0.00, `rgba(0,0,0,${0.70 * depth})`);
    gradient.addColorStop(0.45, `rgba(0,0,0,${0.34 * depth})`);
    gradient.addColorStop(1.00, 'rgba(0,0,0,0)');

    ctx.save();
    ctx.globalCompositeOperation = 'source-over';
    ctx.fillStyle = gradient;
    ctx.beginPath(); ctx.arc(headX, headY, radius, 0, TAU); ctx.fill();
    ctx.restore();
  }

  /**
   * The projector's beam: a cone opening upward out of the aperture.
   *
   * Clipped to a shape that stays inside the panel and fades to nothing before
   * it reaches any edge. The previous version was a circle centred on the
   * bottom edge, so the panel sliced two hard chords out of it -- the light
   * looked cut off rather than cast.
   */
  _drawGlow(ctx, emitter, headY, scale, phase) {
    const top = Math.max(2, headY - scale * 0.36);
    const height = emitter.y - top;
    if (height < 8) return;

    const halfTop = Math.min(scale * 0.34, emitter.cx * 0.88);
    const lit = this.flicker * this.rise;

    // Vertical fade along the beam, and the cone's own taper does the sideways
    // fade -- no blur filter, which is what produced hard rectangular edges.
    const gradient = ctx.createLinearGradient(0, emitter.y, 0, top);
    gradient.addColorStop(0.0, this.palette.at(phase, 0.72, 0.80, 0.20 * lit));
    gradient.addColorStop(0.35, this.palette.at(phase + 0.12, 0.66, 0.78, 0.06 * lit));
    gradient.addColorStop(1.0, this.palette.at(phase + 0.3, 0.62, 0.76, 0));

    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.fillStyle = gradient;

    // Curved sides, so the cone has no hard diagonal edge either.
    ctx.beginPath();
    ctx.moveTo(emitter.cx - emitter.half, emitter.y);
    ctx.quadraticCurveTo(emitter.cx - halfTop * 0.55, top + height * 0.45, emitter.cx - halfTop, top);
    ctx.lineTo(emitter.cx + halfTop, top);
    ctx.quadraticCurveTo(emitter.cx + halfTop * 0.55, top + height * 0.45, emitter.cx + emitter.half, emitter.y);
    ctx.closePath();
    ctx.fill();

    // The aperture itself, bright where the light leaves it.
    const mouth = ctx.createRadialGradient(
      emitter.cx, emitter.y, 0, emitter.cx, emitter.y, emitter.half * 2.6);
    mouth.addColorStop(0, this.palette.at(phase, 0.5, 0.95, 0.55 * lit));
    mouth.addColorStop(1, this.palette.at(phase, 0.6, 0.8, 0));
    ctx.fillStyle = mouth;
    ctx.beginPath();
    ctx.arc(emitter.cx, emitter.y, emitter.half * 2.6, 0, TAU);
    ctx.fill();
    ctx.restore();
  }

  setModel(url, framing = {}) {
    const key = JSON.stringify([url || '', framing]);
    if (key === this._modelKey) return;
    this._modelKey = key;
    if (this._h3) this._h3.cancelled = true;
    this._h3 = new HoloHead(url, framing);
    this._pool = null;
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
function makeParticle(target, copy = 0) {
  const has3d = target.length >= 5;
  const vW = has3d ? target[3] : target[2];
  const hW = (has3d ? target[4] : target[3]) || 0;
  const lip = Math.abs(vW) > LIP.LIP_THRESHOLD || hW > LIP.LIP_THRESHOLD;

  // Copies beyond the first are offset slightly along the surface, so extra
  // density is extra SAMPLING of the same face rather than dots stacked on dots.
  const jitter = copy === 0 ? 0 : 0.0045;
  const p = {
    spread: Math.random(),
    lip,
    off: lip && Math.random() < LIP.DENSITY_DROP,
    jx: (Math.random() - 0.5) * jitter,
    jy: (Math.random() - 0.5) * jitter,
  };
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
