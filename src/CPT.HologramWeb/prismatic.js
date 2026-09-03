// Colour and phase for everything the hologram draws.
//
// This used to draw a border and a spinner of its own. It no longer draws
// anything: the glowing border belongs on the bar itself -- the modal that was
// already there -- and WPF owns that, so the border is drawn there and this
// keeps only the two things the projection needs from it:
//
//   phase   where the colour sweep has got to, so the head, the beam and the
//           bar's border all run off one clock rather than three
//   energy  eased 0..1, so nothing snaps on or off
//
// It stays a separate object because the phase has to keep advancing even when
// the head is parked, or the sweep would stall whenever there was nothing to
// project.

import { makePalette } from './palette.js';

export class Prismatic {
  constructor() {
    this.state = 'idle';
    this.phase = 0;
    this.energy = 0;
    this.palette = makePalette('prismatic');
    this._raf = null;
    this._last = 0;
    this._loop = this._loop.bind(this);
  }

  setPalette(colour) { this.palette = makePalette(colour); this._kick(); }

  start() { this._kick(); }

  setState(s) { this.state = s; this._kick(); }

  _kick() { if (!this._raf) this._raf = requestAnimationFrame(this._loop); }

  _loop(ts) {
    ts = ts || 0;
    const dt = this._last ? Math.min(0.1, (ts - this._last) / 1000) : 1 / 60;
    this._last = ts;

    const lit = this.state === 'busy' || this.state === 'speaking';
    this.energy += ((lit ? 1 : 0) - this.energy) * Math.min(1, 4.2 * dt);
    this.phase = (this.phase + (0.06 + this.energy * 0.24) * dt) % 1;

    // Park at rest: a phase nobody is looking at costs nothing to stop.
    if (lit || this.energy >= 0.01) this._raf = requestAnimationFrame(this._loop);
    else { this._raf = null; this._last = 0; }
  }
}
