// The prismatic glow: a border that traces the whole panel, and the spinner the
// agent's projection comes out of.
//
// Ported from SyntaxEngine (platform/web/player/prismatic.js), which puts this
// ring around its main button. Here the same treatment goes around the entire
// modal, and the ring becomes the loading spinner in the middle of it. The
// behaviour it is careful about is kept:
//
//   • IDLE is a calm, static, dim outline — no motion, barely any glow, so the
//     app never looks like it is working when it is not.
//   • Only a real turn spins and blooms it, and turning off DISSOLVES back to
//     the grey rather than snapping colourless.
//   • Easing and spin are time-based, so the frame cap below does not slow them.
//   • It parks itself completely at rest, costing nothing.

import { makePalette } from './palette.js';

const TAU = Math.PI * 2;

export class Prismatic {
  constructor(canvas) {
    this.c = canvas;
    this.ctx = canvas.getContext('2d');
    this.state = 'idle';
    this.phase = 0;
    this.energy = 0;            // eased 0..1 — 0 at idle (frozen), 1 when busy
    this.palette = makePalette('prismatic');
    this.spinner = null;        // {cx, cy, r} in CSS pixels, filled each frame
    this._raf = null;
    this._last = 0;

    // A slowly spinning, shadow-blurred ring does not need 60fps. Capping it
    // roughly halves the GPU cost of holding this on screen for a long turn.
    this._frameMs = 1000 / 24;
    this._loop = this._loop.bind(this);
  }

  setPalette(colour) { this.palette = makePalette(colour); this._kick(); }

  start() { this._kick(); }

  setState(s) {
    this.state = s;             // only 'busy'/'speaking' animate
    this._kick();
  }

  _kick() { if (!this._raf) this._raf = requestAnimationFrame(this._loop); }

  _resize() {
    // A decorative bloom canvas: a DPR-1 backing store cuts fill about four-fold
    // on a high-density display with no meaningful loss.
    const r = this.c.getBoundingClientRect();
    const w = Math.max(2, r.width | 0), h = Math.max(2, r.height | 0);
    if (this.c.width !== w || this.c.height !== h) { this.c.width = w; this.c.height = h; }
    return { w, h };
  }

  _loop(ts) {
    ts = ts || 0;
    if (this._last && ts - this._last < this._frameMs) { this._raf = requestAnimationFrame(this._loop); return; }
    const dt = this._last ? Math.min(0.1, (ts - this._last) / 1000) : 1 / 60;
    this._last = ts;

    const { w, h } = this._resize();
    const ctx = this.ctx;
    ctx.save();
    ctx.clearRect(0, 0, w, h);

    const lit = this.state === 'busy' || this.state === 'speaking';
    this.energy += ((lit ? 1 : 0) - this.energy) * Math.min(1, 4.2 * dt);
    this.phase = (this.phase + (0.06 + this.energy * 0.24) * dt) % 1;

    // The spinner sits low and centred: the head is projected up out of it, so
    // it belongs near the bar the panel is anchored to.
    const spinnerR = Math.max(14, Math.min(w, h) * 0.085);
    this.spinner = { cx: w / 2, cy: h - spinnerR - 14, r: spinnerR };

    const e = this.energy;
    this._drawBorder(ctx, w, h, e);
    this._drawSpinner(ctx, this.spinner, e);

    ctx.restore();

    if (lit || this.energy >= 0.01) this._raf = requestAnimationFrame(this._loop);
    else { this._raf = null; this._last = 0; }
  }

  /** The glowing outline around the whole modal. */
  _drawBorder(ctx, w, h, e) {
    const inset = 1.5, radius = 12;
    const path = () => roundedRect(ctx, inset, inset, w - inset * 2, h - inset * 2, radius);

    // A glass panel behind everything, so the head reads against a surface
    // rather than against whatever is on the desktop. It is nearly clear at rest
    // -- an always-on dark slab floating over the screen would be intrusive --
    // and firms up into a proper modal while a turn is running.
    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = 0.05 + e * 0.47;
    ctx.fillStyle = '#101216';
    path(); ctx.fill();

    // The calm grey outline is always the base, so the colour above it can fade
    // to nothing without the border disappearing.
    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = 0.45;
    ctx.lineWidth = 1.4;
    ctx.strokeStyle = '#3b3d44';
    path(); ctx.stroke();
    if (e < 0.01) return;

    const sat = 0.12 + e * 0.76, light = 0.46 + e * 0.30;
    const gradient = this._edgeGradient(ctx, w, h, sat, light);

    // Bloom first, then a crisp core on top: the bloom under 'lighter' gives the
    // glow, and normal compositing keeps the core's colours clean.
    ctx.lineCap = 'round';
    ctx.strokeStyle = gradient;
    ctx.globalCompositeOperation = 'lighter';
    ctx.shadowColor = this.palette.at((this.phase + 0.5) % 1, 0.7, 0.72);
    ctx.globalAlpha = (0.12 + e * 0.30) * e;
    ctx.lineWidth = 2 + e * 4;
    ctx.shadowBlur = 4 + e * 16;
    path(); ctx.stroke();

    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = (0.7 + e * 0.3) * e;
    ctx.lineWidth = 1.2 + e * 1.4;
    ctx.shadowColor = this.palette.at((this.phase + 0.5) % 1, 0.6, 0.8);
    ctx.shadowBlur = 1 + e * 4;
    path(); ctx.stroke();
  }

  /**
   * The loading spinner: the same ring, and the mouth the projection pours out
   * of. Its colours run the other way round so the border and the spinner read
   * as one system rather than two clocks.
   */
  _drawSpinner(ctx, s, e) {
    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = 0.6;
    ctx.lineWidth = 1.6;
    ctx.strokeStyle = '#474d59';
    circle(ctx, s.cx, s.cy, s.r); ctx.stroke();
    if (e < 0.01) return;

    const sat = 0.12 + e * 0.76, light = 0.46 + e * 0.30;
    const gradient = ctx.createConicGradient(-Math.PI / 2, s.cx, s.cy);
    const steps = 48;
    for (let i = 0; i <= steps; i++) {
      const p = i / steps;
      gradient.addColorStop(p, this.palette.at(((p + this.phase) % 1 + 1) % 1, sat, light));
    }

    ctx.lineCap = 'round';
    ctx.strokeStyle = gradient;
    ctx.globalCompositeOperation = 'lighter';
    ctx.shadowColor = this.palette.at((this.phase + 0.5) % 1, 0.7, 0.72);
    ctx.globalAlpha = (0.14 + e * 0.34) * e;
    ctx.lineWidth = 2.5 + e * 4;
    ctx.shadowBlur = 5 + e * 18;
    circle(ctx, s.cx, s.cy, s.r); ctx.stroke();

    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = (0.7 + e * 0.3) * e;
    ctx.lineWidth = 1.4 + e * 1.8;
    ctx.shadowBlur = 1 + e * 4;
    circle(ctx, s.cx, s.cy, s.r); ctx.stroke();
  }

  /**
   * A gradient that runs around the panel's edge rather than straight across
   * it, so the colour appears to travel along the border on all four sides.
   */
  _edgeGradient(ctx, w, h, sat, light) {
    const gradient = ctx.createConicGradient(-Math.PI / 2, w / 2, h / 2);
    const steps = 48;
    for (let i = 0; i <= steps; i++) {
      const p = i / steps;
      gradient.addColorStop(p, this.palette.at(((p - this.phase) % 1 + 1) % 1, sat, light));
    }
    return gradient;
  }
}

function circle(ctx, cx, cy, r) { ctx.beginPath(); ctx.arc(cx, cy, r, 0, TAU); }

function roundedRect(ctx, x, y, w, h, r) {
  const radius = Math.min(r, w / 2, h / 2);
  ctx.beginPath();
  ctx.moveTo(x + radius, y);
  ctx.arcTo(x + w, y, x + w, y + h, radius);
  ctx.arcTo(x + w, y + h, x, y + h, radius);
  ctx.arcTo(x, y + h, x, y, radius);
  ctx.arcTo(x, y, x + w, y, radius);
  ctx.closePath();
}
