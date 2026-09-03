// The agent head as a normalised point cloud — the "correct spots" the hologram's
// flowing particles materialise onto.
//
// Ported from SyntaxEngine (platform/web/studio/holo_head.js). Only the baked
// points path is kept: SYNTAX prefers assets/agent_head_points.json over parsing
// a 10 MB GLB at load, and since we ship that 33 KB file there is no reason to
// carry three.js and a GLB loader for a fallback that would never run.
//
// Targets are [x, y, z, jawWeight, lipBandWeight]:
//   x, y   normalised front view, screen-y down
//   z      true mesh depth, front-positive — the nose leads and the ears trail
//          when the head turns
//   jawW   signed, centre-peaked: the lower lip and jaw drop, the upper lip lifts
//   lipW   the lip band including the corners, so the mouth can pull wide (EE)
//          or round in (OO) rather than just opening
//
// The head data is own-generated in the SYNTAX project; no third-party licence
// attaches to it.

const POINTS_URL = 'assets/agent_head_points.json';

export class HoloHead {
  constructor() {
    this.ok = true;
    this.loaded = false;
    this.targets = [];
    this.mouthN = [0, 0.12];
    this._load();
  }

  async _load() {
    try {
      const response = await fetch(POINTS_URL, { cache: 'no-cache' });
      if (!response.ok) { this.ok = false; return; }

      const doc = await response.json();
      if (!Array.isArray(doc?.targets) || doc.targets.length < 200) { this.ok = false; return; }

      this.targets = doc.targets;
      this.mouthN = Array.isArray(doc.mouth) ? doc.mouth : [0, 0.12];
      this.loaded = true;
    } catch {
      // No head data means no head. Nothing else is drawn in its place: a
      // procedural stand-in reads as a bug, not as a face.
      this.ok = false;
    }
  }
}
