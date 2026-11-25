// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The textures of the painted area, one per kind of work (fork only): ploughed, harrowed, rolled, seeded,
// planted, sprayed, fertiliser and manure spread, mown, raked. Each is a seamless tile the app draws once
// (`workTile`): x across the pass, y along it, in metres, one pass wide (two where the passes alternate) and
// WORK_TILE_M long, so it repeats with the passes and lines up with them on the map (`drawCoverageSk`). The
// implement's picture says which work it does (`work` in IMPLEMENT_PICTURES).

const WORK_TILE_M = 4;          // the tile's length along the pass, m
const WORK_TILE_MAX_PX = 1024;  // the tile's widest side: a wide implement gets fewer pixels a metre
const WORK_TILE_PX_PER_M = 100; // pixels a metre, up to that

function _wtHash(ix, iy, seed) {
  let h = (Math.imul(ix, 374761393) + Math.imul(iy, 668265263) + Math.imul(seed, 1442695041)) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177); h ^= h >>> 16;
  return (h >>> 0) / 4294967295;
}
// Value noise on a lattice of about cx by cy (m), repeating every PX by PY (m): the tile's own size, so its edges meet.
function _wtNoise(x, y, cx, cy, PX, PY, seed) {
  const nx = Math.max(1, Math.round(PX / cx)), ny = Math.max(1, Math.round(PY / cy));
  const gx = x * nx / PX, gy = y * ny / PY, ix = Math.floor(gx), iy = Math.floor(gy);
  const fx = gx - ix, fy = gy - iy, sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
  const m = (a, n) => ((a % n) + n) % n;
  const x0 = m(ix, nx), x1 = m(ix + 1, nx), y0 = m(iy, ny), y1 = m(iy + 1, ny);
  const a = _wtHash(x0, y0, seed), b = _wtHash(x1, y0, seed), c = _wtHash(x0, y1, seed), d = _wtHash(x1, y1, seed);
  return (a * (1 - sx) + b * sx) * (1 - sy) + (c * (1 - sx) + d * sx) * sy;
}
function _wtFbm(x, y, cx, cy, PX, PY, seed, oct) {
  let s = 0, a = 0.5, t = 0;
  for (let o = 0; o < oct; o++) { s += a * _wtNoise(x, y, cx, cy, PX, PY, seed + o * 31); t += a; a *= 0.5; cx /= 2; cy /= 2; }
  return s / t;
}
const _wtMix = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const _wtMul = (c, k) => [c[0] * k, c[1] * k, c[2] * k];
const _wtStep = (e0, e1, x) => { const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); };
// A spacing near s that goes a whole number of times into L: rows, furrows and marks meet at the tile's edges.
const _wtFit = (L, s) => L / Math.max(1, Math.round(L / s));

// Each work: `passes` (the tile's width in passes) and `rgb(x, y, W, H, px)`: the colour at (x, y) m of a tile W by
// H m (W the passes' width), px a pixel's size in metres (for the speckles, which are a pixel or two).
const WORK_TEXTURES = {
  // Turned furrow slices every 40 cm: the shadowed furrow wall, the bright crest, the back slope; clods along them.
  ploughed: { passes: 1, rgb(x, y, W, H) {
    const f = _wtFit(W, 0.4), w = (_wtNoise(x, y, 4, 0.9, W, H, 3) - 0.5) * 0.1;
    const u = ((((x + w) % f) + f) % f) / f;
    const p = u < 0.14 ? 0.18 + 2.6 * u : u < 0.4 ? 0.55 + 1.6 * (u - 0.14) : 0.97 - 0.6 * (u - 0.4);
    const clod = _wtFbm(x, y, 0.09, 0.26, W, H, 11, 3), fine = _wtNoise(x, y, 0.03, 0.03, W, H, 5);
    let b = 0.5 + 0.55 * p + 0.45 * (clod - 0.5) + 0.18 * (fine - 0.5);
    if (fine > 0.78 && p > 0.65) b += 0.14;
    return _wtMul([139, 90, 51], b);
  } },
  // Fine crumbly tilth, small clods, faint disc lines every 22 cm.
  harrowed: { passes: 1, rgb(x, y, W, H) {
    const crumb = _wtFbm(x, y, 0.05, 0.05, W, H, 21, 3), clod = _wtNoise(x, y, 0.07, 0.07, W, H, 23);
    const big = _wtNoise(x, y, 0.6, 0.6, W, H, 27), w = (_wtNoise(x, y, 2, 0.7, W, H, 29) - 0.5) * 0.06;
    const f = _wtFit(W, 0.22), u = ((((x + w) % f) + f) % f) / f, line = Math.exp(-Math.pow((u - 0.5) / 0.08, 2));
    let b = 0.82 + 0.55 * (crumb - 0.5) + 0.16 * (big - 0.5) - 0.2 * line;
    if (clod > 0.78) b += 0.3 * (clod - 0.78) / 0.22;
    if (clod < 0.2) b -= 0.18 * (0.2 - clod) / 0.2;
    return _wtMul([146, 104, 68], b);
  } },
  // Smooth pressed soil, the roller's rings along the pass; every other pass a shade lighter.
  rolled: { passes: 2, rgb(x, y, W, H) {
    const pass = x < W ? 1.06 : 0.94;
    const ring = 0.5 + 0.5 * Math.sin(2 * Math.PI * x / _wtFit(W, 0.12)), n = _wtFbm(x, y, 0.4, 0.4, 2 * W, H, 41, 3);
    const f = _wtNoise(x, y, 0.03, 0.03, 2 * W, H, 43);
    return _wtMul([156, 120, 84], pass * (0.9 + 0.1 * ring + 0.12 * (n - 0.5) + 0.06 * (f - 0.5)));
  } },
  // A fine seedbed, a seed row every 12.5 cm.
  seeded: { passes: 1, rgb(x, y, W, H) {
    const f = _wtFit(W, 0.125), u = (x % f) / f, row = Math.exp(-Math.pow((u - 0.5) / 0.09, 2));
    const edge = Math.exp(-Math.pow((Math.abs(u - 0.5) - 0.2) / 0.07, 2));
    const n = _wtFbm(x, y, 0.06, 0.06, W, H, 51, 3), big = _wtNoise(x, y, 0.8, 0.8, W, H, 53);
    return _wtMul([164, 122, 84], 0.95 + 0.22 * (n - 0.5) + 0.1 * (big - 0.5) - 0.32 * row + 0.07 * edge);
  } },
  // A row every 75 cm: the press wheels' two bright marks and the closed seed slot between them.
  planted: { passes: 1, rgb(x, y, W, H) {
    const f = _wtFit(W, 0.75), a = Math.abs((x % f) - f / 2);
    const slot = Math.exp(-Math.pow(a / 0.016, 2)), press = Math.exp(-Math.pow((a - 0.055) / 0.026, 2)), band = _wtStep(0.14, 0.09, a);
    const n = _wtFbm(x, y, 0.07, 0.07, W, H, 61, 3), big = _wtNoise(x, y, 0.9, 0.9, W, H, 63);
    return _wtMul([150, 106, 70], 0.86 + 0.3 * (n - 0.5) * (1 - band * 0.7) + 0.1 * (big - 0.5) + 0.1 * band + 0.14 * press - 0.4 * slot);
  } },
  // The crop, wet (darker, a little blue), droplets catching the light, and the tractor's tramline in each pass.
  sprayed: { passes: 1, rgb(x, y, W, H, px) {
    const track = Math.min(Math.abs(x - (W / 2 - 0.9)), Math.abs(x - (W / 2 + 0.9)));
    const row = 0.5 + 0.5 * Math.cos(2 * Math.PI * x / _wtFit(W, 0.15)), n = _wtFbm(x, y, 0.08, 0.14, W, H, 71, 3);
    const big = _wtNoise(x, y, 1.2, 1.6, W, H, 73);
    let col = _wtMul([70, 128, 64], 0.84 + 0.2 * (n - 0.5) + 0.12 * row + 0.1 * (big - 0.5));
    if (_wtHash(Math.floor(x / px), Math.floor(y / px), 77) > 0.9935) col = _wtMix(col, [215, 240, 250], 0.85);
    if (track < 0.24) {
      const tread = 0.5 + 0.5 * Math.sin(2 * Math.PI * y / 0.1), s = _wtNoise(x, y, 0.05, 0.05, W, H, 79);
      col = _wtMul([118, 96, 66], 0.86 + 0.18 * tread + 0.15 * (s - 0.5));
    } else if (track < 0.3) col = _wtMul(col, 0.8);
    return col;
  } },
  // Granules on the crop: light dots.
  fertiliser: { passes: 1, rgb(x, y, W, H) {
    const n = _wtFbm(x, y, 0.1, 0.1, W, H, 81, 3), big = _wtNoise(x, y, 1.4, 1.4, W, H, 83);
    let col = _wtMul([108, 146, 72], 0.88 + 0.24 * (n - 0.5) + 0.12 * (big - 0.5));
    const g = _wtNoise(x, y, 0.06, 0.06, W, H, 85);
    if (g > 0.8) col = _wtMix(col, [242, 240, 228], Math.min(1, (g - 0.8) / 0.05));
    return col;
  } },
  // Stubble with manure clumps.
  manure: { passes: 1, rgb(x, y, W, H) {
    const st = (y % 0.25) < 0.05 && ((x + (Math.floor(y / 0.25) % 2) * 0.09) % _wtFit(W, 0.24)) < 0.06;
    let col = _wtMul([172, 156, 100], 0.95 + 0.14 * (_wtNoise(x, y, 0.2, 0.2, W, H, 91) - 0.5));
    if (st) col = _wtMul(col, 0.8);
    const c = _wtFbm(x, y, 0.12, 0.2, W, H, 93, 3) * (0.75 + 0.5 * _wtNoise(x, y, 0.6, 0.9, W, H, 97));
    if (c > 0.6) col = _wtMix(col, _wtMul([78, 54, 34], 0.85 + 0.4 * (_wtNoise(x, y, 0.03, 0.03, W, H, 95) - 0.5)), Math.min(1, (c - 0.6) / 0.06));
    return col;
  } },
  // Cut stubble with the mower's marks across, and the swath down the middle of the pass.
  mown: { passes: 1, rgb(x, y, W, H) {
    const c = W / 2, half = Math.min(0.8, W / 4), e = (_wtNoise(x, y, 0.3, 0.25, W, H, 101) - 0.5) * 0.25 * half / 0.8;
    const d = Math.abs(x - c);
    if (d < half + e) {
      const s = _wtFbm(x, y, 0.04, 0.4, W, H, 103, 3);
      return _wtMul([84, 136, 52], 0.8 + 0.45 * (s - 0.5) + 0.18 * (1 - d / half) + (x > c ? -0.05 : 0.05));
    }
    const mark = (y % 0.4) < 0.04 ? 0.9 : 1, n = _wtFbm(x, y, 0.06, 0.12, W, H, 105, 3);
    return _wtMul([170, 200, 124], mark * (0.95 + 0.14 * (n - 0.5)));
  } },
  // Raked stubble with the tines' marks, and the windrow in the middle, its shadow on one side.
  raked: { passes: 1, rgb(x, y, W, H) {
    const c = W / 2, half = Math.min(0.55, W / 6), e = (_wtNoise(x, y, 0.25, 0.22, W, H, 111) - 0.5) * 0.18, d = x - c;
    if (Math.abs(d) < half + e) {
      const s = _wtFbm(x, y, 0.03, 0.3, W, H, 113, 3);
      return _wtMul([110, 152, 62], 0.72 + 0.5 * (s - 0.5) + 0.25 * (1 - Math.abs(d) / (half + 0.05)) + (d > 0.35 * half ? -0.12 : 0));
    }
    // tine marks: arcs every 20 cm, bowed towards the windrow (one tile length holds a whole number of them)
    const arc = Math.abs((((y + 6 * d * d / W) % 0.2) + 0.2) % 0.2 - 0.1) < 0.012;
    let col = _wtMul([194, 196, 134], 0.95 + 0.12 * (_wtFbm(x, y, 0.06, 0.1, W, H, 115, 3) - 0.5));
    if (arc) col = _wtMul(col, 0.9);
    if (d > half + e && d < half + 0.25) col = _wtMul(col, 0.88);
    return col;
  } },
};

// A work's name for the settings' note.
function workName(work) {
  const n = { ploughed: () => tr('Ploughed'), harrowed: () => tr('Harrowed'), rolled: () => tr('Rolled'),
    seeded: () => tr('Seeded'), planted: () => tr('Planted'), sprayed: () => tr('Sprayed'),
    fertiliser: () => tr('Fertiliser spread'), manure: () => tr('Manure spread'), mown: () => tr('Mown'), raked: () => tr('Raked') }[work];
  return n ? n() : work;
}

// The tile of a work for passes `width` m apart, made once per work and width: { canvas, width (m), mean (its mean
// colour [r,g,b] 0-255), luma (the mean of its brightness, 0-1) }. Null for a work that has none.
const _workTiles = new Map();
function workTile(work, width) {
  const tex = WORK_TEXTURES[work];
  if (!tex || !(width > 0.05)) return null;
  const W = width, key = work + '|' + W.toFixed(4);
  let t = _workTiles.get(key);
  if (t) return t;
  const TWm = W * tex.passes, ppm = Math.min(WORK_TILE_PX_PER_M, WORK_TILE_MAX_PX / Math.max(TWm, WORK_TILE_M));
  const TW = Math.max(8, Math.round(TWm * ppm)), TH = Math.max(8, Math.round(WORK_TILE_M * ppm));
  const cv = document.createElement('canvas'); cv.width = TW; cv.height = TH;
  const ctx = cv.getContext('2d'), img = ctx.createImageData(TW, TH), d = img.data;
  const sx = TWm / TW, sy = WORK_TILE_M / TH;
  let r = 0, g = 0, b = 0;
  for (let j = 0; j < TH; j++) for (let i = 0; i < TW; i++) {
    const c = tex.rgb((i + 0.5) * sx, (j + 0.5) * sy, W, WORK_TILE_M, sx), k = (j * TW + i) * 4;
    d[k] = c[0]; d[k + 1] = c[1]; d[k + 2] = c[2]; d[k + 3] = 255; // the array clamps
    r += d[k]; g += d[k + 1]; b += d[k + 2];
  }
  ctx.putImageData(img, 0, 0);
  const n = TW * TH, mean = [r / n, g / n, b / n];
  t = { canvas: cv, width: TWm, mean, luma: (mean[0] * 0.3 + mean[1] * 0.59 + mean[2] * 0.11) / 255, skImg: null };
  if (_workTiles.size > 8) { for (const old of _workTiles.values()) if (old.skImg) old.skImg.delete(); _workTiles.clear(); }
  _workTiles.set(key, t);
  return t;
}
