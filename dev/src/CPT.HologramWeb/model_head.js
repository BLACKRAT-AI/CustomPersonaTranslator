import * as THREE from 'three';
import { GLTFLoader } from './vendor/GLTFLoader.js';
import { frameHead } from './head_framing.js';

let cachedUrl, cachedSamples;
export async function loadModelHead(url, options) {
  if (url !== cachedUrl) {
    cachedUrl = url;
    cachedSamples = sampleModel(url).catch(error => { cachedUrl = null; throw error; });
  }
  return frameHead(await cachedSamples, options);
}

async function sampleModel(url) {
  const loader = new GLTFLoader();
  const gltf = await loader.loadAsync(url);
  const root = gltf.scene;
  root.updateMatrixWorld(true);
  const triangles = [], headTriangles = [];
  let total = 0, headTotal = 0;
  const a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3();
  const cross = new THREE.Vector3(), side = new THREE.Vector3();
  const isHead = name => /head|face|skull/i.test(name || '') && !/headphone/i.test(name || '');
  root.traverse(mesh => {
    if (!mesh.isMesh || !mesh.geometry?.attributes.position) return;
    let node = mesh, named = false;
    while (node && node !== root) { named ||= isHead(node.name); node = node.parent; }
    const geometry = mesh.geometry, index = geometry.index;
    const count = index ? index.count : geometry.attributes.position.count;
    const indices = geometry.attributes.skinIndex, weights = geometry.attributes.skinWeight;
    if (mesh.skeleton) mesh.skeleton.update();
    const headWeight = i => {
      if (!mesh.skeleton || !indices || !weights) return 0;
      let weight = 0;
      for (let k = 0; k < 4; k++) if (isHead(mesh.skeleton.bones[indices.getComponent(i, k)]?.name)) weight += weights.getComponent(i, k);
      return weight;
    };
    const stride = 3 * Math.max(1, Math.ceil(count / 600000));
    for (let i = 0; i + 2 < count; i += stride) {
      const ids = [0, 1, 2].map(j => index ? index.getX(i + j) : i + j);
      mesh.getVertexPosition(ids[0], a).applyMatrix4(mesh.matrixWorld);
      mesh.getVertexPosition(ids[1], b).applyMatrix4(mesh.matrixWorld);
      mesh.getVertexPosition(ids[2], c).applyMatrix4(mesh.matrixWorld);
      const area = cross.subVectors(b, a).cross(side.subVectors(c, a)).length() / 2;
      if (!Number.isFinite(area) || area <= 0) continue;
      const head = named || ids.every(id => headWeight(id) >= 0.35);
      total += area;
      const triangle = {a: a.toArray(), b: b.toArray(), c: c.toArray(), head, end: total};
      triangles.push(triangle);
      if (head) { headTotal += area; headTriangles.push({...triangle, end: headTotal}); }
    }
  });
  if (!triangles.length) throw new Error('No triangles found. Export the model as a GLB mesh.');
  let seed = 7341;
  const random = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296; };
  const sample = (list, area, count) => Array.from({length: count}, () => {
    const target = random() * area;
    let lo = 0, hi = list.length - 1;
    while (lo < hi) { const mid = (lo + hi) >> 1; if (list[mid].end < target) lo = mid + 1; else hi = mid; }
    const t = list[lo], u = Math.sqrt(random()), v = random();
    return [...t.a.map((n, i) => n * (1 - u) + t.b[i] * u * (1 - v) + t.c[i] * u * v), t.head];
  });
  const points = sample(triangles, total, 16000);
  if (headTriangles.length) points.push(...sample(headTriangles, headTotal, 8000));
  root.traverse(mesh => {
    mesh.geometry?.dispose();
    for (const material of (Array.isArray(mesh.material) ? mesh.material : mesh.material ? [mesh.material] : [])) {
      for (const value of Object.values(material)) if (value?.isTexture) { value.image?.close?.(); value.dispose(); }
      material.dispose();
    }
  });
  return points;
}
