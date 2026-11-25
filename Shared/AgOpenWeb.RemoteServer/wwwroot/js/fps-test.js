// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The frame rate test (fork only). Every page loads this file, but it only declares startFpsTest(), which frame.js
// calls at its end when the address asks for the test: http://<host>:5174/?fpstest. It moves the camera through a
// set of views (close and far, turning, tilted, zooming in and out, the whole painted area) and draws each with the
// painted area in its three looks (plain colour, the work's texture, the colour with the work's pattern): the frames
// a second and the draw time each costs on this screen. Only this page is touched: the look is set here (the tool
// profile is not changed), the camera is put back at the end, and nothing goes to the host but the result lines, for
// its log (diag.page, which an observer may send too). While it runs the frame cap is off (frame.js: 30 a second),
// so the numbers say how far this screen can go.

function startFpsTest() {
  const SETTLE_S = 1, MEASURE_S = 5, SLOW_MS = 50;
  const LOOKS = [[PAINTED_AREA.plain, 'plain'], [PAINTED_AREA.work, 'texture'], [PAINTED_AREA.colourWithPattern, 'colour+pattern']];
  const TAU = 2 * Math.PI;

  // ---- the panel ----
  const panel = document.createElement('div');
  panel.id = 'fpstest';
  panel.style.cssText = 'position:fixed;left:calc(var(--sal, 0px) + 8px);top:8px;z-index:80;max-width:min(620px,94vw);'
    + 'max-height:86vh;overflow:auto;background:rgba(0,0,0,0.86);color:#fff;border-radius:8px;padding:10px 12px;'
    + 'font-size:13px;line-height:1.35;box-shadow:0 2px 12px rgba(0,0,0,0.5)';
  panel.innerHTML = '<div style="font-weight:700;font-size:15px;margin-bottom:4px"></div><div class="ft-msg"></div>'
    + '<div class="ft-out" style="font-family:monospace;font-size:12px;white-space:pre-wrap;margin-top:6px"></div>'
    + '<div style="margin-top:8px;display:flex;gap:8px;flex-wrap:wrap">'
    + '<button class="ft-start"></button><button class="ft-save"></button><button class="ft-close"></button></div>';
  const btnCss = 'height:38px;padding:0 16px;border-radius:6px;border:1px solid #888;background:#2b2b2b;color:#fff;font-weight:600';
  panel.firstChild.textContent = tr('Frame rate test');
  const msg = panel.querySelector('.ft-msg'), out = panel.querySelector('.ft-out');
  const bStart = panel.querySelector('.ft-start'), bSave = panel.querySelector('.ft-save'), bClose = panel.querySelector('.ft-close');
  for (const b of [bStart, bSave, bClose]) b.style.cssText = btnCss;
  bStart.textContent = tr('Start'); bSave.textContent = tr('Save the results'); bClose.textContent = tr('Close');
  const show = (b, on) => { b.style.display = on ? '' : 'none'; };
  show(bSave, false);
  document.body.appendChild(panel);

  let run = null, report = '';

  function ready() {
    if (!CK || !skSurface) return tr('Waiting for the map to load...');
    return '';
  }
  function idleText() {
    const r = ready();
    if (r) return r;
    const painted = cov && cov.skImg && covCells > 0;
    return (painted ? '' : tr('No painted area on the map: open a field with a job that has painted area for the texture to be measured.') + '\n')
      + tr('About 2 minutes. The camera moves by itself and comes back where it was; the painted area is shown in its three looks on this screen only. Park the tractor first.');
  }
  const idle = setInterval(() => {
    if (run) return;
    if (!report) msg.textContent = idleText();
    bStart.disabled = !!ready();
  }, 500);
  msg.style.whiteSpace = 'pre-wrap';

  // ---- where the painted area is ----
  // The coverage image drawn down to at most 128 px a side and read back: the painted spots' places.
  function paintedSamples() {
    if (!cov || !cov.skImg) return null;
    const w = cov.width, h = cov.height, cs = cov.cellSize, k = 128 / Math.max(w, h);
    const tw = Math.max(1, Math.round(w * k)), th = Math.max(1, Math.round(h * k));
    let px = null;
    try {
      if (grCtx) {
        const s = CK.MakeRenderTarget(grCtx, tw, th);
        if (s) {
          const c = s.getCanvas();
          c.clear(CK.TRANSPARENT);
          c.drawImageRectOptions(cov.skImg, CK.XYWHRect(0, 0, w, h), CK.XYWHRect(0, 0, tw, th), CK.FilterMode.Linear, CK.MipmapMode.None, null);
          s.flush();
          const img = s.makeImageSnapshot();
          px = img.readPixels(0, 0, { width: tw, height: th, colorType: CK.ColorType.RGBA_8888, alphaType: CK.AlphaType.Unpremul, colorSpace: CK.ColorSpace.SRGB });
          img.delete(); s.delete();
        }
      }
      if (!px && cov.canvas) {
        const c = document.createElement('canvas'); c.width = tw; c.height = th;
        const x = c.getContext('2d'); x.drawImage(cov.canvas, 0, 0, tw, th);
        px = x.getImageData(0, 0, tw, th).data;
      }
    } catch (e) { px = null; }
    if (!px) return null;
    const on = new Uint8Array(tw * th);
    for (let i = 0; i < tw * th; i++) on[i] = px[i * 4 + 3] > 60 ? 1 : 0;
    return { on, tw, th, at: (i, j) => ({ e: cov.originE + (i + 0.5) / k * cs, n: cov.originN + (h - (j + 0.5) / k) * cs }) };
  }
  // The middle of the largest painted stretch (the spot with the most painted spots round it) and the painted area's
  // bounds; the field's middle when nothing is painted.
  function placeOfTest() {
    const s = paintedSamples();
    let best = null, bestN = -1, minE = Infinity, minN = Infinity, maxE = -Infinity, maxN = -Infinity;
    if (s) {
      const R = 3;
      for (let j = 0; j < s.th; j++) for (let i = 0; i < s.tw; i++) {
        if (!s.on[j * s.tw + i]) continue;
        const p = s.at(i, j);
        minE = Math.min(minE, p.e); maxE = Math.max(maxE, p.e); minN = Math.min(minN, p.n); maxN = Math.max(maxN, p.n);
        let n = 0;
        for (let b = Math.max(0, j - R); b <= Math.min(s.th - 1, j + R); b++)
          for (let a = Math.max(0, i - R); a <= Math.min(s.tw - 1, i + R); a++) n += s.on[b * s.tw + a];
        if (n > bestN) { bestN = n; best = p; }
      }
    }
    if (best) return { p: best, b: { minE, minN, maxE, maxN }, painted: true };
    const ring = scene && scene.boundaries && scene.boundaries[0];
    if (ring && ring.length) {
      for (const q of ring) { minE = Math.min(minE, q.e); maxE = Math.max(maxE, q.e); minN = Math.min(minN, q.n); maxN = Math.max(maxN, q.n); }
      return { p: { e: (minE + maxE) / 2, n: (minN + maxN) / 2 }, b: { minE, minN, maxE, maxN }, painted: false };
    }
    return { p: { e: camE, n: camN }, b: { minE: camE - 100, minN: camN - 100, maxE: camE + 100, maxN: camN + 100 }, painted: false };
  }

  // A picture with a work for this tool and vehicle (the one chosen if it has one): the texture needs one.
  function workPicture() {
    const tl = (config && config.tool) || {}, vt = ((config && config.vehicle) || {}).type | 0;
    const fits = q => q.work && q.type === (tl.type | 0) && q.vehicles.includes(vt);
    const q = IMPLEMENT_PICTURES.find(r => r.id === (tl.picture | 0) && fits(r))
      || IMPLEMENT_PICTURES.find(fits) || IMPLEMENT_PICTURES.find(r => r.work && r.vehicles.includes(vt));
    return q ? { id: q.id, type: q.type, work: q.work } : null;
  }

  // ---- the views: the camera at a fraction f (0 to 1) of the step ----
  function views(at) {
    const P = at.p, B = at.b, R = mapRotation;
    const fitZ = Math.max(0.2, Math.min(vw / ((B.maxE - B.minE) * 1.15 + 1), vh / ((B.maxN - B.minN) * 1.15 + 1)));
    const C = { e: (B.minE + B.maxE) / 2, n: (B.minN + B.maxN) / 2 };
    const circle = (r, f) => ({ e: P.e + r * Math.cos(TAU * f), n: P.n + r * Math.sin(TAU * f) });
    const tri = f => 1 - Math.abs(2 * f - 1);
    return [
      { name: 'top, 40 px/m, moving', cam: f => ({ ...circle(6, f), z: 40, pitch: 0, rot: R }) },
      { name: 'top, 12 px/m, turning', cam: f => ({ ...circle(3, f), z: 12, pitch: 0, rot: R + TAU * f }) },
      { name: 'top, 4 px/m, moving', cam: f => ({ ...circle(25, f), z: 4, pitch: 0, rot: R }) },
      { name: 'whole painted area', cam: f => ({ e: C.e, n: C.n, z: fitZ, pitch: 0, rot: R + 0.5 * Math.PI * f }) },
      { name: '3D 60 deg, 12 px/m, turning', cam: f => ({ ...circle(3, f), z: 12, pitch: DEFAULT_PITCH, rot: R + TAU * f }) },
      { name: '3D 65 deg, 40 px/m, moving', cam: f => ({ ...circle(6, f), z: 40, pitch: MAX_PITCH, rot: R }) },
      { name: 'zoom 2-60 px/m, 3D 30 deg', cam: f => ({ e: P.e, n: P.n, z: 2 * Math.pow(30, tri(f)), pitch: Math.PI / 6, rot: R + Math.PI * f }) },
    ];
  }

  // ---- the run, a step a frame: frame.js calls updateCamera once each frame ----
  const _updateCamera = updateCamera, _noteDrawMs = noteDrawMs;
  updateCamera = function () { if (run) runFrame(performance.now()); return _updateCamera.apply(this, arguments); };
  noteDrawMs = function (ms) {
    if (run && run.m) { const m = run.m; m.draws++; m.ms += ms; if (ms > m.max) m.max = ms; }
    return _noteDrawMs(ms);
  };

  // The look on this page only: a copy of the host's config with the tool's picture and look changed. A Config frame
  // from the host in the meantime is copied again.
  function applyLook(look) {
    if (config !== run.mine) {
      run.host = config;
      const tool = Object.assign({}, config.tool, run.pic ? { picture: run.pic.id, type: run.pic.type } : {}, { paintedArea: look });
      run.mine = Object.assign({}, config, { tool });
      config = run.mine;
    } else config.tool.paintedArea = look;
  }

  function runFrame(now) {
    const st = run.steps[run.i];
    _fullRateUntil = now + 1000;   // no frame cap while the test runs
    _viewSaveT = now;              // the moving camera is not saved as the view (maybeSaveView)
    if (!st.t0) { st.t0 = now; applyLook(st.look); }
    const T = SETTLE_S + MEASURE_S, t = (now - st.t0) / 1000;
    const c = st.view.cam(Math.min(1, t / T));
    cameraMode = 2; camE = c.e; camN = c.n; pxPerM = c.z; pitch = c.pitch; mapRotation = c.rot;
    if (t >= SETTLE_S && !run.m) run.m = { t0: now, draws: 0, ms: 0, max: 0, slow: 0, last: now };
    if (run.m) {
      if (now - run.m.last > SLOW_MS) run.m.slow++;
      run.m.last = now;
    }
    if (t >= T) {
      const m = run.m, s = (now - m.t0) / 1000;
      st.r = { fps: m.draws / s, ms: m.ms / Math.max(1, m.draws), max: m.max, slow: m.slow };
      run.m = null;
      run.i++;
      if (run.i >= run.steps.length) { finish(); return; }
    }
    if (run.shown !== run.i) { // the text only when the step changes: a layout every frame would be measured too
      run.shown = run.i;
      msg.textContent = tr('Step {i} of {n}: {view}, {look}', { i: run.i + 1, n: run.steps.length, view: run.steps[run.i].view.name, look: run.steps[run.i].lookName });
    }
  }

  function start() {
    if (ready()) return;
    const at = placeOfTest(), pic = workPicture();
    const saved = { cameraMode, camE, camN, pxPerM, pitch, mapRotation };
    let tileMs = null;
    if (pic && typeof workTile === 'function') {
      const w = toolWidthM() - ((config.tool && config.tool.overlap) || 0);
      const t0 = performance.now(); workTile(pic.work, w + 0.0003); tileMs = performance.now() - t0; // a fresh tile, timed
      workTile(pic.work, w);
    }
    const looks = at.painted && pic ? LOOKS : [[PAINTED_AREA.plain, 'plain']];
    const steps = [];
    for (const view of views(at)) for (const [look, lookName] of looks) steps.push({ view, look, lookName });
    run = { steps, i: 0, m: null, pic, saved, at, tileMs, host: config, mine: null, looks, shown: -1 };
    out.textContent = ''; report = ''; show(bSave, false);
    show(bStart, false); bClose.textContent = tr('Stop');
    panel.style.maxWidth = '360px';
  }

  function restore() {
    const s = run.saved;
    if (config === run.mine) config = run.host;
    configDirty = true;
    cameraMode = s.cameraMode; camE = s.camE; camN = s.camN; pxPerM = s.pxPerM; pitch = s.pitch; mapRotation = s.mapRotation;
    _fullRateUntil = 0;
  }

  function finish() {
    const r = run; restore(); run = null;
    const pad = (x, n) => String(x).padEnd(n), lpad = (x, n) => String(x).padStart(n);
    const g = typeof glInfo === 'function' ? (() => { try { return glInfo(); } catch (e) { return {}; } })() : {};
    const lines = [];
    lines.push('AgOpenWeb frame rate test, ' + new Date().toISOString().slice(0, 16).replace('T', ' ') + ' UTC');
    lines.push('Screen: ' + Math.round(vw) + 'x' + Math.round(vh) + ' CSS px, pixel ratio ' + dpr + ' (' + ckcv.width + 'x' + ckcv.height + ' px drawn)');
    lines.push('Graphics: ' + (g.renderer || '?') + ' (' + (g.kind || '?') + ')' + (drawnByCpu() ? ', drawn by the CPU: the texture is not used here' : ''));
    lines.push('Browser: ' + navigator.userAgent);
    if (r.pic) lines.push('Texture: ' + r.pic.work + ' (picture ' + r.pic.id + '), tile made in ' + (r.tileMs != null ? r.tileMs.toFixed(0) + ' ms' : '?'));
    if (!r.at.painted) lines.push('No painted area on the map: plain colour only.');
    lines.push('Frame cap off for the test (the app draws at most 30 a second).');
    lines.push('');
    const head = pad('view', 30) + r.looks.map(([, n]) => lpad(n, 16)).join('');
    lines.push(head);
    lines.push(pad('', 30) + r.looks.map(() => lpad('fps / draw ms', 16)).join(''));
    const vnames = [...new Set(r.steps.map(s => s.view.name))];
    const ratios = [];
    for (const v of vnames) {
      const row = r.steps.filter(s => s.view.name === v);
      lines.push(pad(v, 30) + row.map(s => lpad(s.r.fps.toFixed(1) + ' / ' + s.r.ms.toFixed(1), 16)).join(''));
      const plain = row.find(s => s.look === PAINTED_AREA.plain), tex = row.find(s => s.look === PAINTED_AREA.work);
      if (plain && tex && plain.r.fps > 0) ratios.push([v, tex.r.fps / plain.r.fps]);
    }
    lines.push('');
    lines.push('Frames over ' + SLOW_MS + ' ms: ' + r.looks.map(([look, n]) =>
      n + ' ' + r.steps.filter(s => s.look === look).reduce((a, s) => a + s.r.slow, 0)).join(', ')
      + ' (in ' + vnames.length * MEASURE_S + ' s each)');
    if (ratios.length) {
      const mean = ratios.reduce((a, [, x]) => a + x, 0) / ratios.length;
      const worst = ratios.reduce((a, b) => (b[1] < a[1] ? b : a));
      lines.push('Texture against plain: ' + pct(mean) + ' fps on average, worst ' + pct(worst[1]) + ' (' + worst[0] + ').');
    }
    report = lines.join('\n');
    out.textContent = report;
    msg.textContent = tr('Done. The results are in the log too (a Bug Report Dump carries them).');
    for (const l of lines) if (l) transport.send('diag.page|FPS test: ' + l.replace(/\s+/g, ' ').slice(0, 280));
    show(bStart, true); bStart.textContent = tr('Run again'); show(bSave, true); bClose.textContent = tr('Close');
    panel.style.maxWidth = 'min(620px,94vw)';
  }
  const pct = x => (x >= 1 ? '+' : '') + ((x - 1) * 100).toFixed(0) + ' %';

  bStart.addEventListener('click', start);
  bClose.addEventListener('click', () => {
    if (run) { restore(); run = null; report = ''; out.textContent = ''; show(bStart, true); bClose.textContent = tr('Close'); panel.style.maxWidth = 'min(620px,94vw)'; return; }
    clearInterval(idle); panel.remove();
  });
  bSave.addEventListener('click', () => {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([report + '\n'], { type: 'text/plain' }));
    a.download = 'fps-test-' + new Date().toISOString().slice(0, 16).replace(/[-:T]/g, '') + '.txt';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  });
}
