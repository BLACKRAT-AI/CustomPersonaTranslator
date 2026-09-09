// Pure framing math, shared by live rendering and geometry regression checks.
export function frameHead(samples, options = {}) {
  const mode = options.mode || 'auto';
  let points = samples;
  let status = 'Whole model';
  const bounds = ps => ps.reduce((b, p) => b.map((v, i) => i < 3 ? Math.min(v, p[i]) : Math.max(v, p[i - 3])), [Infinity, Infinity, Infinity, -Infinity, -Infinity, -Infinity]);
  const whole = bounds(points);
  if (!points.length || !whole.every(Number.isFinite)) throw new Error('The model has no usable mesh surface.');
  const named = samples.filter(p => p[3]);
  if (mode === 'auto' && named.length >= 100) {
    points = named;
    status = 'Head detected from mesh or skeleton';
  } else if (mode === 'upper' || (mode === 'auto' && whole[4] - whole[1] > 1.6 * (whole[3] - whole[0]))) {
    const fraction = Math.max(0.1, Math.min(0.6, Number(options.fraction) || 0.25));
    const cutoff = whole[4] - (whole[4] - whole[1]) * fraction;
    points = points.filter(p => p[1] >= cutoff);
    status = 'Upper model framed - adjust Head area if needed';
  } else if (mode === 'auto') status = 'Bust framed - choose Upper model for a full character';
  if (points.length < 20) throw new Error('Too little geometry in the head area. Increase Head area or choose Whole model.');
  const rotation = (Number(options.rotation) || 0) * Math.PI / 180;
  const c = Math.cos(rotation), s = Math.sin(rotation);
  points = points.map(p => [p[0] * c + p[2] * s, p[1], -p[0] * s + p[2] * c]);
  const b = bounds(points), cx = (b[0] + b[3]) / 2, cy = (b[1] + b[4]) / 2, cz = (b[2] + b[5]) / 2;
  const scale = Math.max(b[3] - b[0], b[4] - b[1], 0.000001);
  const zoom = Math.max(0.5, Math.min(2, Number(options.zoom) || 1));
  // Keep the forward surface; rotation lets the user choose which side faces us.
  points = points.filter(p => p[2] >= cz - (b[5] - b[2]) * 0.15);
  const step = Math.max(1, Math.ceil(points.length / 1600));
  const targets = points.filter((_, i) => i % step === 0).map(p => {
    const x = (p[0] - cx) / scale, y = -(p[1] - cy) / scale;
    const dx = x / 0.22, dy = (y - 0.12) / 0.11;
    const jaw = Math.max(0, 1 - Math.hypot(dx, dy)) * (y > 0.12 ? 1 : -0.5);
    const bx = x / 0.17, by = (y - 0.12) / 0.07;
    const lip = bx * bx + by * by < 1 ? 1 - Math.abs(by) : 0;
    return [x * zoom, y * zoom, (p[2] - cz) / scale * zoom, jaw, lip];
  });
  return {targets, mouth: [0, 0.12 * zoom], status};
}
