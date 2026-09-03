// The hologram's colour.
//
// The default is SYNTAX's prismatic sweep: the full spectrum, scrolling, so the
// spinner's rotation and the particle flow are legible as motion. A persona can
// instead pick a single colour — but a single flat hue kills that legibility,
// the ring turns into an undifferentiated band and you can no longer see it
// turn. So a chosen colour is never drawn flat: it is given a narrow hue spread
// and a lightness sweep around itself, which reads as that colour while keeping
// every moving thing readable.

/** Full spectrum, the SYNTAX look. */
export const PRISMATIC = 'prismatic';

/** How far a tinted palette drifts either side of its hue, in turns (≈±22°). */
const TINT_HUE_SPREAD = 0.06;

/** How much a tinted palette varies lightness, so movement still reads. */
const TINT_LIGHT_SPREAD = 0.16;

/**
 * Builds the colour source used by every part of the hologram.
 *
 * @param {string} colour  "prismatic", or any CSS hex the persona chose.
 * @returns {{prismatic: boolean, hue: number, at: (p: number, s: number, l: number, a?: number) => string}}
 *   `at(p, s, l, a)` is the colour at parametric position p (0..1 around the
 *   ring, along the beam, across the face), with the saturation and lightness
 *   the caller would have used for the prismatic version.
 */
export function makePalette(colour) {
  const prismatic = !colour || String(colour).trim().toLowerCase() === PRISMATIC;
  if (prismatic) {
    return {
      prismatic: true,
      hue: 0,
      at: (p, s, l, a = 1) => hsla(p, s, l, a),
    };
  }

  const base = hexToHue(colour);
  return {
    prismatic: false,
    hue: base.h,
    at: (p, s, l, a = 1) => {
      // A full turn of p sweeps once through the narrow band and back, so the
      // colour cycles smoothly rather than jumping at the wrap point.
      const wave = Math.sin(p * Math.PI * 2);
      return hsla(
        base.h + wave * TINT_HUE_SPREAD,
        Math.min(1, s * 0.35 + base.s * 0.65),
        Math.max(0.08, Math.min(0.96, l + wave * TINT_LIGHT_SPREAD)),
        a);
    },
  };
}

/** hsl() from 0..1 inputs, wrapping hue. */
export function hsla(h, s, l, a = 1) {
  const hue = ((((h % 1) + 1) % 1) * 360) | 0;
  return `hsla(${hue} ${(s * 100) | 0}% ${(l * 100) | 0}% / ${a})`;
}

/** Hue and saturation of a #rgb / #rrggbb colour, as 0..1. Falls back to cyan. */
function hexToHue(colour) {
  const hex = String(colour).trim().replace('#', '');
  const full = hex.length === 3 ? hex.split('').map((c) => c + c).join('') : hex;
  if (!/^[0-9a-f]{6}$/i.test(full)) return { h: 0.51, s: 0.8 };

  const r = parseInt(full.slice(0, 2), 16) / 255;
  const g = parseInt(full.slice(2, 4), 16) / 255;
  const b = parseInt(full.slice(4, 6), 16) / 255;

  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  if (d === 0) return { h: 0, s: 0 };

  let h;
  if (max === r) h = ((g - b) / d) % 6;
  else if (max === g) h = (b - r) / d + 2;
  else h = (r - g) / d + 4;

  const light = (max + min) / 2;
  return { h: (h / 6 + 1) % 1, s: d / (1 - Math.abs(2 * light - 1) || 1) };
}
