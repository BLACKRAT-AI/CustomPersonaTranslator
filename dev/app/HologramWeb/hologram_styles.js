// Alternate projections share the exact face deformation and gaze of particles.
const TAU = Math.PI * 2;
const edgesByHead = new WeakMap();
const vaporSprites = new Map();

export function laserEdges(targets) {
  if (edgesByHead.has(targets)) return edgesByHead.get(targets);
  const cells = new Map(), size = 0.085, edges = [];
  const coord = p => [Math.floor(p[0] / size), Math.floor(p[1] / size), Math.floor((p.length >= 5 ? p[2] : 0) / size)];
  for (let i = 0; i < targets.length; i += 2) {
    const key = coord(targets[i]).join(',');
    if (!cells.has(key)) cells.set(key, []);
    cells.get(key).push(i);
  }
  for (let i = 0; i < targets.length; i += 2) {
    const p = targets[i], cell = coord(p), near = [];
    for (let x = -1; x <= 1; x++) for (let y = -1; y <= 1; y++) for (let z = -1; z <= 1; z++) {
      for (const j of cells.get([cell[0] + x, cell[1] + y, cell[2] + z].join(',')) || []) {
        if (j <= i) continue;
        const q = targets[j], distance = Math.hypot(p[0] - q[0], p[1] - q[1], (p.length >= 5 ? p[2] : 0) - (q.length >= 5 ? q[2] : 0));
        if (distance > 0.007 && distance < 0.12) near.push([j, distance]);
      }
    }
    near.sort((a, b) => a[1] - b[1]);
    for (const [j] of near.slice(0, 3)) edges.push([i, j]);
  }
  edgesByHead.set(targets, edges);
  return edges;
}

export function scanRows(points, spacing = 3) {
  const rows = new Map();
  for (const p of points) {
    const y = Math.round(p.y / spacing) * spacing;
    if (!rows.has(y)) rows.set(y, []);
    rows.get(y).push(p);
  }
  return [...rows].sort((a, b) => a[0] - b[0]).map(([y, ps]) => [y, ps.sort((a, b) => a.x - b.x)]);
}

export function drawProjectionStyle(ctx, style, points, scene) {
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';
  if (style === 'scanlines') scanlines(ctx, points, scene);
  else if (style === 'steam') steam(ctx, points, scene);
  else lasers(ctx, points, scene);
  ctx.restore();
}

// Reconstruct a continuous visible surface once per head. Scan bands are a
// transmission effect over that surface, never curved lines joining particles.
const triangleCache = new WeakMap();
export function surfaceTriangles(targets) {
  if (triangleCache.has(targets)) return triangleCache.get(targets);
  if (targets.length < 3) return [];
  const ps = targets.map(p => [p[0], p[1]]), n = ps.length;
  const xs = ps.map(p => p[0]), ys = ps.map(p => p[1]);
  const loX = Math.min(...xs), hiX = Math.max(...xs), loY = Math.min(...ys), hiY = Math.max(...ys);
  const span = Math.max(hiX - loX, hiY - loY, 0.001), cx = (loX + hiX) / 2, cy = (loY + hiY) / 2;
  ps.push([cx - 20 * span, cy - 10 * span], [cx, cy + 20 * span], [cx + 20 * span, cy - 10 * span]);
  const triangle = (a, b, c) => {
    const [ax, ay] = ps[a], [bx, by] = ps[b], [cx, cy] = ps[c];
    const d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
    if (Math.abs(d) < 1e-12) return null;
    const aa = ax * ax + ay * ay, bb = bx * bx + by * by, cc = cx * cx + cy * cy;
    const x = (aa * (by - cy) + bb * (cy - ay) + cc * (ay - by)) / d;
    const y = (aa * (cx - bx) + bb * (ax - cx) + cc * (bx - ax)) / d;
    return {a, b, c, x, y, r: (x - ax) ** 2 + (y - ay) ** 2};
  };
  let tris = [triangle(n, n + 1, n + 2)];
  for (let i = 0; i < n; i++) {
    const boundary = new Map(), kept = [];
    for (const t of tris) {
      if ((ps[i][0] - t.x) ** 2 + (ps[i][1] - t.y) ** 2 > t.r + 1e-12) { kept.push(t); continue; }
      for (const [a, b] of [[t.a, t.b], [t.b, t.c], [t.c, t.a]]) {
        const key = a < b ? a + ':' + b : b + ':' + a;
        if (boundary.has(key)) boundary.delete(key); else boundary.set(key, [a, b]);
      }
    }
    for (const [a, b] of boundary.values()) { const t = triangle(a, b, i); if (t) kept.push(t); }
    tris = kept;
  }
  const limit = span * (n < 20 ? 2 : 0.13);
  const result = tris.filter(t => t.a < n && t.b < n && t.c < n)
    .map(t => [t.a, t.b, t.c]).filter(t => t.every((a, i) => {
      const b = t[(i + 1) % 3]; return Math.hypot(ps[a][0] - ps[b][0], ps[a][1] - ps[b][1]) < limit;
    }));
  triangleCache.set(targets, result);
  return result;
}

function scanlines(ctx, points, s) {
  if (!points.length) return;
  const hue = s.palette.prismatic ? 201 : s.palette.hue * 360;
  const time = s.reduced ? 0 : s.t;
  const spacing = Math.max(3.2, s.scale / 85);
  const rows = scanRows(points, spacing);
  const top = rows[0][0], bottom = rows[rows.length - 1][0];
  const scan = top + ((time * 0.16 + 0.3) % 1) * (bottom - top);
  const flicker = s.reduced ? 1 : 0.88 + 0.07 * Math.sin(time * 17) + 0.05 * Math.sin(time * 29);
  const seed = n => { const v = Math.sin(n * 127.1 + 311.7) * 43758.5453; return v - Math.floor(v); };
  // Light exists only in transmitted fragments. There is no filled mesh or
  // opaque body underneath: the desktop remains visible through the projection.
  ctx.lineCap = 'round';
  for (let row = 0; row < rows.length; row++) {
    const [y, samples] = rows[row];
    const transmission = seed(row + Math.floor(time * 9) * 0.13);
    if (transmission < 0.09) continue;
    const dropout = Math.exp(-(((y - scan) / 12) ** 2));
    const shift = s.reduced ? 0 : Math.sin(time * 8 + row * 0.04) * 0.6
      + (dropout > 0.7 ? Math.sin(time * 31) * 3 : 0);
    for (let i = 0; i < samples.length; i++) {
      const p = samples[i], noise = seed(row * 53 + i * 7);
      if (noise < 0.2) continue;
      const depth = Math.max(0.12, Math.min(1, 0.38 + p.z * 2));
      const next = samples[i + 1];
      const span = Math.min(s.scale * 0.024, next ? Math.max(1.5, next.x - p.x) : 2.5);
      const alpha = s.resolve * flicker * (0.24 + depth * 0.65) * (0.5 + noise * 0.5) * (1 - dropout * 0.8);
      ctx.strokeStyle = `hsla(${hue},90%,72%,${alpha})`;
      ctx.lineWidth = 0.65 + noise * 0.45;
      ctx.beginPath(); ctx.moveTo(p.x + shift - 0.6, y); ctx.lineTo(p.x + shift + span, y); ctx.stroke();
      // Sparse optical bloom follows individual emissions, not the whole face.
      if (noise > 0.55) {
        ctx.strokeStyle = `hsla(${hue},95%,65%,${alpha * 0.14})`;
        ctx.lineWidth = 4;
        ctx.stroke();
      }
    }
    // Intermittent edge light trails dissolve outward instead of enclosing a shell.
    if (samples.length > 2 && seed(row * 3) > 0.6) {
      for (const [p, direction] of [[samples[0], -1], [samples[samples.length - 1], 1]]) {
        const x = p.x + shift, length = 4 + seed(row) * 12;
        const gradient = ctx.createLinearGradient(x, y, x + direction * length, y);
        gradient.addColorStop(0, `hsla(${hue},95%,78%,${s.resolve * 0.23 * flicker})`);
        gradient.addColorStop(1, `hsla(${hue},95%,65%,0)`);
        ctx.strokeStyle = gradient; ctx.lineWidth = 0.8;
        ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + direction * length, y); ctx.stroke();
      }
    }
  }
}

function vaporSprite(color) {
  if (vaporSprites.has(color)) return vaporSprites.get(color);
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 48;
  const c = canvas.getContext('2d');
  const g = c.createRadialGradient(24, 24, 0, 24, 24, 24);
  g.addColorStop(0, color); g.addColorStop(0.28, color); g.addColorStop(1, 'transparent');
  c.fillStyle = g; c.fillRect(0, 0, 48, 48);
  if (vaporSprites.size > 64) vaporSprites.clear();
  vaporSprites.set(color, canvas);
  return canvas;
}

function steam(ctx, points, s) {
  const phase = Math.floor(s.phase * 16) / 16;
  const sprites = Array.from({length: 6}, (_, i) => vaporSprite(s.palette.at(i / 12 - phase, 0.4, 0.78)));
  const time = s.reduced ? 0 : s.t;
  const stride = Math.max(1, Math.floor(points.length / 1000));
  // Tight luminous vapor defines the face; a second, lower-opacity cloud softens it.
  for (let i = 0; i < points.length; i += stride) {
    const p = points[i], seed = i * 2.399963;
    const curl = Math.sin(seed + time * 0.65);
    const x = p.x + curl * s.scale * 0.005;
    const y = p.y + Math.cos(seed * 0.73 + time * 0.48) * s.scale * 0.004;
    const radius = s.scale * (0.022 + 0.013 * (1 + Math.sin(seed)) / 2);
    ctx.globalAlpha = s.resolve * (0.09 + Math.max(0, p.z) * 0.1);
    ctx.drawImage(sprites[i % 6], x - radius, y - radius, radius * 2, radius * 2);
    if (i % 3 === 0) {
      ctx.globalAlpha = s.resolve * 0.025;
      ctx.drawImage(sprites[i % 6], x - radius * 2.5, y - radius * 2.5, radius * 5, radius * 5);
    }
  }
  // Slow buoyant wisps leave the crown and dissolve, while the face stays anchored.
  const crown = points.filter(p => p.y < s.headY - s.scale * 0.24);
  for (let i = 0; i < Math.min(42, crown.length); i++) {
    const p = crown[(i * 17) % crown.length];
    const life = (time * 0.13 + i * 0.618034) % 1;
    const alpha = Math.sin(life * Math.PI) * (1 - life);
    const radius = s.scale * (0.016 + life * 0.022);
    const x = p.x + Math.sin(i + life * 5) * s.scale * 0.023;
    const y = p.y - life * s.scale * 0.21;
    ctx.globalAlpha = s.resolve * alpha * 0.19;
    ctx.drawImage(sprites[i % 6], x - radius, y - radius, radius * 2, radius * 2);
  }  // Broad, curling filaments lend the vapor a direction, not just a blur.
  ctx.strokeStyle = s.palette.at(0.15 - phase, 0.25, 0.82);
  ctx.lineCap = 'round';
  for (let i = 0; i < Math.min(26, crown.length); i++) {
    const p = crown[(i * 29) % crown.length];
    const life = (time * 0.1 + i * 0.381966) % 1;
    const drift = Math.sin(i + life * 4) * s.scale * 0.04;
    const y = p.y - life * s.scale * 0.2;
    ctx.globalAlpha = Math.sin(life * Math.PI) * (1 - life) * s.resolve * 0.12;
    ctx.lineWidth = s.scale * 0.013;
    ctx.beginPath(); ctx.moveTo(p.x, y);
    ctx.bezierCurveTo(p.x + drift, y - s.scale * 0.035, p.x - drift, y - s.scale * 0.07, p.x + drift * 0.5, y - s.scale * 0.1);
    ctx.stroke();
  }

}

function lasers(ctx, points, s) {
  if (!points.length) return;
  const rows = scanRows(points, Math.max(2.5, s.scale / 110));
  const hue = s.palette.prismatic ? 180 : s.palette.hue * 360;
  const time = s.reduced ? 0.42 : s.t * 0.8;
  const lanes = 3;
  // A raster projector draws successive strips. Phosphor-like persistence
  // makes the previous passes readable while the active beam is unmistakable.
  for (let lane = 0; lane < lanes; lane++) {
    const start = Math.floor(rows.length * lane / lanes);
    const end = Math.floor(rows.length * (lane + 1) / lanes);
    const count = Math.max(1, end - start), phase = (time + lane * 0.31) % 1;
    const current = phase * count;
    for (let r = start; r < end; r++) {
      const [y, samples] = rows[r];
      const age = ((current - (r - start)) / count + 1) % 1;
      const persistence = 0.1 + 0.85 * Math.exp(-age * 3.8);
      for (let i = 0; i < samples.length - 1; i++) {
        const a = samples[i], b = samples[i + 1];
        if (b.x - a.x > s.scale * 0.035) continue;
        const depth = Math.max(0.2, Math.min(1, 0.55 + a.z * 1.5));
        ctx.strokeStyle = `hsla(${hue},100%,65%,${s.resolve * persistence * depth})`;
        ctx.lineWidth = 0.75;
        ctx.beginPath(); ctx.moveTo(a.x, y); ctx.lineTo(b.x, y); ctx.stroke();
      }
    }
    const [y, samples] = rows[Math.min(end - 1, start + Math.floor(current))];
    const sweep = (current % 1);
    const index = Math.min(samples.length - 1, Math.floor(sweep * samples.length));
    const hit = samples[index], x = hit.x;
    const originX = s.emitter.cx + (lane - 1) * 5, originY = s.emitter.y;
    // Broad faint scattering, saturated beam, then a narrow hot core.
    for (const [width, alpha, light] of [[7, 0.035, 55], [2.2, 0.24, 62], [0.65, 0.8, 85]]) {
      ctx.strokeStyle = `hsla(${hue},100%,${light}%,${s.resolve * alpha})`;
      ctx.lineWidth = width; ctx.beginPath(); ctx.moveTo(originX, originY); ctx.lineTo(x, y); ctx.stroke();
    }
    const glow = ctx.createRadialGradient(x, y, 0, x, y, 9);
    glow.addColorStop(0, `hsla(${hue},100%,95%,${s.resolve})`);
    glow.addColorStop(0.16, `hsla(${hue},100%,75%,${s.resolve * 0.8})`);
    glow.addColorStop(1, `hsla(${hue},100%,65%,0)`);
    ctx.fillStyle = glow; ctx.beginPath(); ctx.arc(x, y, 9, 0, TAU); ctx.fill();
    ctx.fillStyle = `hsla(${hue},100%,92%,${s.resolve})`;
    ctx.beginPath(); ctx.arc(originX, originY, 1.5, 0, TAU); ctx.fill();
  }
}
