/* LinBench front end */
(function () {
  'use strict';
  const $ = (s, r) => (r || document).querySelector(s);
  const $$ = (s, r) => [...(r || document).querySelectorAll(s)];
  const fmt = LB.fmt;
  const cssv = (n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
  const S = (k) => cssv('--s' + k);
  const TWO_PI = 2 * Math.PI;

  // ------------------------------------------------------------------ API
  // results that depend only on the request are kept for the session (cleared on a new analysis
  // or a region change), so going back to a tab or operating point is instant
  const CACHEABLE = /^\/api\/(tf|time|sweep|structure|structure_sweep|closed_time|closed_tf|modes|campbell|delay|interaction)\b/;
  async function api(url, body) {
    const key = CACHEABLE.test(url) ? 'api:' + url + (body === undefined ? '' : JSON.stringify(body)) : null;
    if (key && st.cache[key]) return st.cache[key];
    const p = (async () => {
      const r = await fetch(url, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const j = await r.json();
      if (!r.ok || j.error) throw new Error(j.error || ('HTTP ' + r.status));
      return j;
    })();
    if (key) { st.cache[key] = p; p.catch(() => { if (st.cache[key] === p) delete st.cache[key]; }); }
    return p;
  }

  // ------------------------------------------------------------------ state
  const st = {
    summary: null, op: 0, tab: 'ol', sub: (() => { try { const v = JSON.parse(localGet('lb.sub', 'null')); if (v && v.ol && v.cl) return v; } catch (e) { /* ignore */ } return { ol: 'schedule', cl: 'summary' }; })(),
    freq: localGet('lb.freq', 'rad'), io: null, tio: null, cio: null, charts: [],
    cache: {}, campbell: { all: false, damped: false, fmax: null, fams: null },
  };
  function localGet(k, d) { try { return localStorage.getItem(k) || d; } catch (e) { return d; } }
  function localSet(k, v) { try { localStorage.setItem(k, v); } catch (e) { /* ignore */ } }

  // ------------------------------------------------------------------ helpers
  function h(tag, cls, parent, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    if (parent) parent.appendChild(e);
    return e;
  }
  const db = (v) => (v > 0 ? 20 * Math.log10(v) : null);
  const wconv = (w) => (st.freq === 'hz' ? w / TWO_PI : w);
  const wunit = () => (st.freq === 'hz' ? 'Hz' : 'rad/s');
  const wlabel = () => `Frequency [${wunit()}]`;
  function fw(w) { return w === null || w === undefined ? '–' : fmt(wconv(w), 3) + ' ' + wunit(); }
  function deg(r) { return r * 180 / Math.PI; }
  function metric(parent, k, v, unit, cls) {
    const m = h('div', 'm' + (cls ? ' ' + cls : ''), parent);
    h('span', 'k', m, k);
    const vv = h('span', 'v', m, v);
    if (unit) h('small', null, vv, unit);
    return m;
  }
  function destroyCharts() { st.charts = []; }

  /** Card with title, sub-title, tool buttons, metric strip and a chart host.  As in TuneMe, a chart's tools
   *  (Enlarge, zoom, Box zoom, Reset, exports) show while the pointer is on the card. */
  function card(parent, title, sub, opts) {
    opts = opts || {};
    const c = h('div', opts.nochart ? 'card' : 'card chart-card', parent);
    const hd = h('div', 'card-h', c);
    h('h3', null, hd, title);
    h('span', 'sub', hd, sub || '');
    const tools = h('div', opts.nochart ? 'tools' : 'tools chart-tools', hd);
    const met = h('div', 'metrics', c);
    const extra = h('div', null, c);
    const host = opts.nochart ? null : h('div', null, c);
    const obj = { el: c, head: hd, tools, metrics: met, extra, host, chart: null };
    if (!opts.nochart) {
      const mk = (label, fn, tip) => { const b = h('button', 'tool', tools, label); b.title = tip; b.onclick = fn; return b; };
      if (!opts.big) mk('Enlarge', () => {
        const all = [...c.querySelectorAll('.lb-chart')].map(e => e._lbChart).filter(Boolean);     // e.g. Bode magnitude and phase
        if (all.length || obj.chart) enlarge(all.length ? all : [obj.chart], title, sub);
      }, 'Show this plot as large as the window');
      mk('+', () => obj.chart && obj.chart.zoomBy(0.7), 'Zoom in (or Ctrl + wheel / pinch on the plot)');
      mk('−', () => obj.chart && obj.chart.zoomBy(1 / 0.7), 'Zoom out');
      const bx = mk('Box zoom', () => {
        obj.box = !obj.box; bx.classList.toggle('on', obj.box);
        $$('.lb-chart', c).forEach(e => e._lbChart && e._lbChart.setDragMode(obj.box ? 'box' : 'pan'));
      }, 'Drag pans the plot; switch on to drag a zoom box instead (Shift+drag does the same)');
      mk('Reset', () => obj.chart && obj.chart.reset(), 'Reset view (or double-click the plot)');
      mk('PNG', () => obj.chart && obj.chart.exportPNG(slug(title)), 'Download image');
      mk('SVG', () => obj.chart && obj.chart.exportSVG(slug(title)), 'Download vector image');
      mk('CSV', () => obj.chart && obj.chart.exportCSV(slug(title)), 'Download data');
    }
    return obj;
  }
  /** The plots of a card as large as the window (stacked, sharing the height as on the page, x linked again where
   *  it was), with the same curves hidden or shown and the same view. */
  function enlarge(chs, title, sub) {
    const mc = $('#modalCard');
    mc.className = 'modal-card wide'; mc.innerHTML = '';
    const head = h('div', 'modal-head', mc);
    h('h2', null, head, title);
    h('span', 'muted', head, sub || '');
    h('span', 'grow', head);
    const close = h('button', 'btn primary', head, 'Close');
    const box = h('div', null, mc); box.style.cssText = 'flex:1;min-height:0;display:flex;flex-direction:column';
    $('#modal').hidden = false;
    const c2 = card(box, title, '', { big: true });
    c2.el.classList.add('big');
    const total = Math.max(340, window.innerHeight - 240), sumH = chs.reduce((a, ch) => a + ch.opts.height, 0);
    const bigs = chs.map((ch, k) => {
      const o2 = Object.assign({}, ch.opts, { height: Math.max(180, Math.floor(total * ch.opts.height / sumH)), fixedHeight: true, hiddenInit: [...ch.hidden] });
      const big = k === 0 ? chartIn(c2, o2) : new LB.Chart(h('div', null, c2.el), o2);
      if (ch.view) { big.view = JSON.parse(JSON.stringify(ch.view)); big.render(); }
      return big;
    });
    if (bigs.length > 1 && chs[0].linked.length) LB.linkX(bigs);
    const done = () => { $('#modal').hidden = true; mc.innerHTML = ''; mc.className = 'modal-card'; document.removeEventListener('keydown', esc); };
    const esc = (e) => { if (e.key === 'Escape') done(); };
    close.onclick = done;
    document.addEventListener('keydown', esc);
  }
  function slug(t) { return (st.summary ? st.summary.info.name + '_' : '') + t.replace(/[^\w]+/g, '_').replace(/^_|_$/g, ''); }
  function chartIn(c, opts) { c.chart = new LB.Chart(c.host, Object.assign({ title: c.el.querySelector('h3').textContent, dragMode: c.box ? 'box' : 'pan' }, opts)); st.charts.push(c.chart); return c.chart; }
  function loading(parent, msg) { return h('div', 'spinner', parent, msg || 'Computing…'); }
  function errorBox(parent, e) { const d = h('div', 'empty', parent); d.textContent = '⚠ ' + (e.message || e); return d; }
  function seqRamp(n) {
    const steps = ['#9ec5f4', '#86b6ef', '#6da7ec', '#5598e7', '#3987e5', '#2a78d6', '#256abf', '#1c5cab', '#184f95', '#104281', '#0d366b'];
    const out = [];
    for (let i = 0; i < n; i++) out.push(steps[Math.round(i / Math.max(1, n - 1) * (steps.length - 1))]);
    return out;
  }

  // ------------------------------------------------------------------ pole/residue models
  // The server sends G(s) = d + sum r_k/(s - p_k) (upper half of each conjugate pair, im > 0 means
  // 'add the conjugate'), checked against its state-space result.  Plots evaluate it over whatever
  // window is on screen, so curves continue when panned or zoomed out.  Mirrors lti.modal_* .
  const MOD = {
    freq(m, w) {
      const n = w.length, re = new Float64Array(n), im = new Float64Array(n), P = m.p, R = m.r, K = P.length;
      for (let i = 0; i < n; i++) {
        let gr = m.d, gi = 0; const wi = w[i];
        for (let k = 0; k < K; k++) {
          const pr = P[k][0], pi = P[k][1], rr = R[k][0], ri = R[k][1];
          let dr = -pr, di = wi - pi, q = dr * dr + di * di;
          gr += (rr * dr + ri * di) / q; gi += (ri * dr - rr * di) / q;
          if (pi > 0) { di = wi + pi; q = dr * dr + di * di; gr += (rr * dr - ri * di) / q; gi += (-ri * dr - rr * di) / q; }
        }
        re[i] = gr; im[i] = gi;
      }
      return { re, im };
    },
    _phi(w, q) {   // continuous arg(j w - q)
      const a = -q[0], y = w - q[1];
      if (a > 1e-300) return Math.atan(y / a);
      if (a < -1e-300) return Math.PI - Math.atan(y / -a);
      return y > 0 ? Math.PI / 2 : (y < 0 ? -Math.PI / 2 : 0);
    },
    /** Unwrapped phase [deg]; independent of the plotted window when the pole-zero form is valid. */
    phase(m, w, G) {
      const n = w.length, out = new Array(n);
      if (m.phase_ok) {
        const pf = [];
        m.p.forEach(p => { pf.push(p); if (p[1] > 0) pf.push([p[0], -p[1]]); });
        for (let i = 0; i < n; i++) {
          let th = m.argk - m.off;
          for (const z of m.z) th += MOD._phi(w[i], z);
          for (const p of pf) th -= MOD._phi(w[i], p);
          const g = Math.atan2(G.im[i], G.re[i]);
          out[i] = (g + TWO_PI * Math.round((th - g) / TWO_PI)) * 180 / Math.PI;
        }
        return out;
      }
      let off = 0, prev = null;
      for (let i = 0; i < n; i++) {
        const g = Math.atan2(G.im[i], G.re[i]);
        if (prev !== null) off -= TWO_PI * Math.round((g + off - prev) / TWO_PI);
        prev = g + off; out[i] = prev * 180 / Math.PI;
      }
      return out;
    },
    time(m, t, kind, amp) {
      const n = t.length, y = new Array(n), P = m.p, R = m.r, K = P.length;
      for (let i = 0; i < n; i++) {
        const ti = t[i];
        if (ti < 0) { y[i] = 0; continue; }
        let s = kind === 'impulse' ? 0 : m.d;
        for (let k = 0; k < K; k++) {
          const pr = P[k][0], pi = P[k][1], rr = R[k][0], ri = R[k][1], wgt = pi > 0 ? 2 : 1;
          const e = Math.exp(pr * ti), er = e * Math.cos(pi * ti), ei = e * Math.sin(pi * ti);
          if (kind === 'impulse') { s += wgt * (rr * er - ri * ei); continue; }
          if (Math.hypot(pr, pi) < 1e-12) { s += wgt * rr * ti; continue; }
          const nr = er - 1, ni = ei, q = pr * pr + pi * pi;                    // (e^{pt} - 1) / p
          const fr = (nr * pr + ni * pi) / q, fi = (ni * pr - nr * pi) / q;
          s += wgt * (rr * fr - ri * fi);
        }
        y[i] = amp * s;
      }
      return y;
    },
  };
  /** Log frequency grid over the visible display range, densified at lightly damped poles/zeros. */
  function fgrid(xd, n, models) {
    const f = st.freq === 'hz' ? TWO_PI : 1;
    const ws = wgrid(xd[0] * f, xd[1] * f, n, models);
    return { x: ws.map(v => v / f), w: ws };
  }
  function wgrid(w0, w1, n, models) {
    if (!(w0 > 0 && w1 > w0)) return [];
    const a = Math.log10(w0), b = Math.log10(w1), w = [];
    for (let i = 0; i < n; i++) w.push(Math.pow(10, a + (b - a) * i / (n - 1)));
    for (const m of models) for (const q of (m ? m.p.concat(m.z || []) : [])) {
      const wn = Math.hypot(q[0], q[1]); if (!(wn > w0 && wn < w1)) continue;
      const z = -q[0] / wn; if (Math.abs(z) > 0.3) continue;
      const bw = Math.max(4 * Math.abs(z), 0.004);
      for (let k = -20; k <= 20; k++) w.push(wn * (1 + bw * k / 20));
    }
    w.sort((p, q) => p - q);
    return w.filter((v, i) => v >= w0 && v <= w1 && (i === 0 || v > w[i - 1]));
  }
  /** Series function for a frequency-response plot: kind 'db' | 'phase' | 'S' | 'T' | 'abs'. */
  function fxFreq(m, kind) {
    if (!m) return undefined;
    return (xd, n) => {
      const g = fgrid(xd, n, [m]), G = MOD.freq(m, g.w);
      if (kind === 'phase') return { x: g.x, y: MOD.phase(m, g.w, G) };
      const y = g.w.map((_, i) => {
        const re = G.re[i], im = G.im[i], L = Math.hypot(re, im), one = Math.hypot(1 + re, im);
        const v = kind === 'S' ? 1 / one : kind === 'T' ? L / one : L;
        return kind === 'abs' ? v : db(v);
      });
      return { x: g.x, y };
    };
  }
  function fxTime(m, kind, amp) {
    if (!m) return undefined;
    return (xd, n) => {
      const t = []; for (let i = 0; i < n; i++) t.push(xd[0] + (xd[1] - xd[0]) * i / (n - 1));
      return { x: t, y: MOD.time(m, t, kind, amp) };
    };
  }

  // ================================================================== SETUP SCREEN
  const IN = { lin: $('#inLin'), discon: $('#inDiscon'), yaml: $('#inYaml'), cp: $('#inCp'), fst: $('#inFst') };
  let mode = 'both', inspectTimer = null, lastInspect = null;

  function showScreen(id) {
    ['setup', 'progress', 'results'].forEach(s => { $('#' + s).hidden = s !== id; });
    $('#btnNew').hidden = id !== 'results';
    $('#btnLog').hidden = !(id === 'results' && PG.charts);
    $('#btnReport').hidden = id !== 'results';
    $('#btnInsp').hidden = id !== 'results';
    $('#hdrTurbine').hidden = id !== 'results';
    document.body.classList.toggle('no-nav', id !== 'results');      // the view list belongs to the results
    $('#workspace').scrollTop = 0;
  }
  function showSetup() { showScreen('setup'); loadRecent(); sbResults(null); $('#sbPath').hidden = true; }

  $$('#inMode button').forEach(b => b.onclick = () => { $$('#inMode button').forEach(x => x.classList.toggle('on', x === b)); mode = b.dataset.v; });
  Object.values(IN).forEach(i => i.addEventListener('input', () => { clearTimeout(inspectTimer); inspectTimer = setTimeout(inspect, 450); }));
  $('#inRecursive').onchange = inspect;

  async function inspect() {
    const body = { lin_dir: IN.lin.value.trim(), discon: IN.discon.value.trim(), yaml: IN.yaml.value.trim(), cp: IN.cp.value.trim(), fst: IN.fst.value.trim() };
    try {
      const r = await api('/api/inspect', body);
      lastInspect = r;
      const map = { lin_dir: IN.lin, discon: IN.discon, yaml: IN.yaml, cp: IN.cp, fst: IN.fst };
      const found = $('#foundMsg');
      found.innerHTML = '';
      for (const k in r.found) if (!map[k].value.trim()) { map[k].value = r[k]; }
      const names = { lin_dir: 'Linearisation folder', discon: 'DISCON', yaml: 'YAML', cp: 'Cp table', fst: 'OpenFAST model' };
      const f = Object.entries(r.found).map(([k, v]) => `${names[k]} ${v}`);
      if (f.length) h('span', null, found, '✓ ' + f.join(' · '));
      (r.messages || []).forEach(m => h('span', 'w', found, m));
      renderScan(r.scan);
    } catch (e) { $('#foundMsg').innerHTML = `<span class="w">${e.message}</span>`; }
  }

  function renderScan(scan) {
    const body = $('#scanBody');
    $('#btnRun').disabled = !(scan && scan.ops && scan.ops.length);
    if (!scan) { body.className = 'scan-empty'; body.textContent = 'Choose a linearisation folder.'; $('#scanMeta').textContent = ''; return; }
    body.className = '';
    body.innerHTML = '';
    $('#scanMeta').textContent = `${scan.n_files} files · ${scan.ops.length} operating points`;
    if (!scan.ops.length) { h('div', 'scan-empty', body, 'No OpenFAST .lin files in this folder.'); return; }
    if (scan.name_order_differs) h('div', 'scan-note', body, 'Files are ordered by the wind speed and azimuth written inside each file, not by file name.');
    (scan.warnings || []).forEach(w => h('div', 'scan-warn', body, '⚠ ' + w));
    (scan.skipped || []).slice(0, 5).forEach(s => h('div', 'scan-warn', body, `Skipped ${s.file}: ${s.reason}`));
    const wrap = h('div', 'tbl-wrap', body);
    const t = h('table', 'tbl', wrap);
    t.innerHTML = '<thead><tr><th>#</th><th>Wind [m/s]</th><th>Rotor [rpm]</th><th>Files</th><th class="l">Azimuths</th><th class="l">First files</th><th class="l">Notes</th></tr></thead>';
    const tb = h('tbody', null, t);
    scan.ops.forEach((o, i) => {
      const tr = h('tr', null, tb);
      h('td', 'num', tr, i + 1);
      h('td', 'num', tr, fmt(o.wind_speed, 4));
      const oc = o.omega_check || {};
      const rtd = h('td', 'num', tr, fmt(o.rotor_rpm, 4) + (oc.spread > 1e-4 ? ` ±${fmt(oc.spread * 50, 2)} %` : ''));
      if (oc.level === 'warning') { rtd.style.color = cssv('--crit-ink'); rtd.style.fontWeight = 600; }
      if (oc.spread > 1e-4) rtd.title = `rotor speed across azimuths: ${(o.file_rpm || []).map(v => fmt(v, 5)).join(', ')} rpm (${oc.kind})`;
      h('td', 'num', tr, o.n_files);
      const az = h('td', 'l', tr);
      const bar = h('span', 'azbar', az);
      bar.title = o.azimuth_deg.map(a => a.toFixed(0) + '°').join(', ');
      const bins = new Array(12).fill(0);
      o.azimuth_deg.forEach(a => { bins[Math.floor(((a % 360) + 360) % 360 / 30)] += 1; });
      bins.forEach(b => { const i2 = h('i', null, bar); i2.style.opacity = b ? 1 : 0.15; });
      h('td', 'l', tr, o.files.slice(0, 3).join(', ') + (o.files.length > 3 ? ' …' : ''));
      h('td', 'l', tr, (o.warnings || []).join('; '));
    });
  }

  // file pickers
  $$('[data-pick]').forEach(b => b.onclick = async () => {
    const kind = b.dataset.pick, target = $('#' + b.dataset.for);
    try {
      const r = await api('/api/pick', { kind, start: target.value.trim() || (IN.lin.value.trim()) });
      if (r.ok && r.path === '__NO_TK__') return openBrowser(kind, target);
      if (!r.ok) return openBrowser(kind, target);
      if (r.path) { target.value = r.path; inspect(); }
    } catch (e) { openBrowser(kind, target); }
  });

  // in-app browser
  let brTarget = null, brKind = 'dir', brSel = null;
  function openBrowser(kind, target) {
    brTarget = target; brKind = kind; brSel = null;
    $('#brTitle').textContent = kind === 'dir' ? 'Select the linearisation folder' : 'Select file';
    $('#browser').hidden = false;
    browseTo(target.value.trim() || '');
  }
  async function browseTo(p) {
    try {
      const r = await api('/api/browse?path=' + encodeURIComponent(p));
      $('#brPath').value = r.path;
      $('#brUp').onclick = () => browseTo(r.parent === null ? r.path : r.parent);
      const L = $('#brList'); L.innerHTML = '';
      brSel = kindIsDir() ? r.path : null;
      $('#brInfo').textContent = kindIsDir() ? (r.n_lin ? `${r.n_lin} .lin files here` : '') : '';
      r.dirs.forEach(d => { const it = h('div', 'br-item', L); it.innerHTML = `<span><span class="ic">▸</span>${d.name}</span>`; it.onclick = () => browseTo(d.path); });
      if (!kindIsDir()) r.files.forEach(f => {
        const it = h('div', 'br-item', L); it.innerHTML = `<span><span class="ic">·</span>${f.name}</span>`;
        it.onclick = () => { $$('.br-item', L).forEach(x => x.classList.remove('sel')); it.classList.add('sel'); brSel = f.path; };
        it.ondblclick = () => { brSel = f.path; $('#brSelect').click(); };
      });
    } catch (e) { $('#brInfo').textContent = e.message; }
  }
  const kindIsDir = () => brKind === 'dir';
  $('#brPath').addEventListener('keydown', e => { if (e.key === 'Enter') browseTo($('#brPath').value); });
  $('#brClose').onclick = () => { $('#browser').hidden = true; };
  $('#brSelect').onclick = () => { if (brSel && brTarget) { brTarget.value = brSel; inspect(); } $('#browser').hidden = true; };

  async function loadRecent() {
    try {
      const r = await api('/api/recent');
      $('#recentCard').hidden = !r.length;
      const L = $('#recentList'); L.innerHTML = '';
      r.forEach(c => {
        const it = h('div', 'ritem', L);
        h('code', null, it, c.lin_dir + (c.discon ? '   +   ' + c.discon.split(/[\\/]/).pop() : ''));
        const b = h('button', 'btn sm', it, 'Use');
        b.onclick = () => { IN.lin.value = c.lin_dir || ''; IN.discon.value = c.discon || ''; IN.yaml.value = c.yaml || ''; IN.cp.value = c.cp || ''; IN.fst.value = c.fst || ''; inspect(); };
      });
    } catch (e) { /* ignore */ }
  }

  $('#btnRun').onclick = async () => {
    const cfg = { lin_dir: IN.lin.value.trim(), discon: IN.discon.value.trim(), yaml: IN.yaml.value.trim(), cp: IN.cp.value.trim(),
      fst: IN.fst.value.trim(), mode, remove_azimuth: $('#inAz').checked, recursive: $('#inRecursive').checked };
    if (mode !== 'open' && !cfg.discon) cfg.mode = 'open';     // closed loop needs a DISCON.IN
    $('#btnRun').disabled = true;
    try { await api('/api/load', cfg); } catch (e) { $('#btnRun').disabled = false; alert(e.message); return; }
    showScreen('progress');
    $('#progErr').hidden = true; $('#progBack').hidden = true;
    startProgress(cfg);
    poll();
  };

  // ------------------------------------------------------------------ live analysis view
  const STAGES = [
    ['scan', 'Scan and group the .lin files'],
    ['read', 'Read, MBC transform, azimuth average'],
    ['modes', 'Eigen-analysis and mode labels'],
    ['campbell', 'Track modes across wind speeds'],
    ['closed', 'Close the ROSCO loop at every point'],
    ['summary', 'Design-model comparison and summary'],
  ];
  const MATH = {
    scan: `<h4>Grouping operating points</h4>
      Every <code>.lin</code> header states the wind speed <i>U</i>, rotor speed Ω and azimuth ψ. Files are grouped by (<i>U</i>, Ω) and sorted by ψ; file names are never used.
      <div class="why">Why it matters: sorting by name puts <code>lin_10</code> before <code>lin_2</code> and silently scrambles a sweep.</div>`,
    read: `<h4>Multi-blade coordinate (Coleman) transform</h4>
      Blade <i>b</i> sits at ψ<sub>b</sub> = ψ + 2π(<i>b</i>−1)/3. Each blade triplet maps to collective, cosine and sine components:
      <div class="eq">x<sub>rot</sub> = T(ψ) x<sub>nr</sub>,&nbsp;&nbsp; T₁ rows = [1, cos ψ<sub>b</sub>, sin ψ<sub>b</sub>]</div>
      <div class="eq">A<sub>nr</sub> = T⁻¹(A T − Ṫ),&nbsp; B<sub>nr</sub> = T⁻¹ B T₁,&nbsp; C<sub>nr</sub> = T₁⁻¹ C T,&nbsp; Ṫ = Ω T₂</div>
      then averaged over the azimuths: <span class="eq" style="display:inline-block;padding:2px 8px;margin:0">Ā = (1/N) Σ<sub>k</sub> A<sub>nr</sub>(ψ<sub>k</sub>)</span>
      <div class="why">The periodicity index max<sub>k</sub>‖A(ψ<sub>k</sub>) − Ā‖/‖Ā‖ is plotted below before and after MBC. It drops sharply only when the transform is right, and what remains is genuine rotor anisotropy. The generator-azimuth position state is then removed, as ROSCO does.</div>`,
    modes: `<h4>Eigenvalues, damping and mode labels</h4>
      <div class="eq">A v<sub>k</sub> = λ<sub>k</sub> v<sub>k</sub>,&nbsp;&nbsp; W = V⁻¹ (left eigenvectors)</div>
      <div class="eq">f<sub>n</sub> = |λ|/2π,&nbsp;&nbsp; ζ = −Re λ / |λ|,&nbsp;&nbsp; stable ⇔ Re λ &lt; 0 for all λ</div>
      Labels come from participation factors <span class="eq" style="display:inline-block;padding:2px 8px;margin:0">p<sub>ik</sub> = |w<sub>ki</sub> v<sub>ik</sub>|</span>, summed per physical DOF. Unlike the largest eigenvector entry, they do not depend on state units.
      For rotor modes the MBC components give the symmetry: collective, or cyclic with forward whirl if |C + jS| ≫ |C − jS| and backward whirl if the reverse.`,
    campbell: `<h4>Following each mode across wind speeds</h4>
      <div class="eq">MAC<sub>ij</sub> = |v<sub>i</sub>ᴴ v<sub>j</sub>|² / (‖v<sub>i</sub>‖² ‖v<sub>j</sub>‖²)</div>
      <div class="eq">cost<sub>ij</sub> = (1 − MAC<sub>ij</sub>) + 0.6 |ln(f<sub>j</sub>/f<sub>i</sub>)| + family penalty</div>
      Modes at neighbouring wind speeds are paired by optimal (Hungarian) assignment on this cost, so lines follow mode shapes and do not jump where two frequencies cross.`,
    closed: `<h4>Closing the ROSCO loop</h4>
      The controller is rebuilt from DISCON.IN: generator-speed low-pass → notches → PI gain-scheduled on pitch (+ floating feedback) → pitch actuator. The loop is broken at the actuator:
      <div class="eq">L(s) = −K(s) G(s),&nbsp;&nbsp; closed loop stable ⇔ all eigenvalues of A<sub>cl</sub> have Re λ &lt; 0</div>
      <div class="eq">GM at ∠L = −180°,&nbsp; PM at |L| = 1,&nbsp; s<sub>m</sub> = min<sub>ω</sub> |1 + L(jω)|,&nbsp; Nyquist check Z = N + P</div>
      <div class="why">Every crossing is root-found, not read off a grid. The eigenvalue count and the Nyquist encirclement count must agree at every point; the log flags any point where they differ.</div>`,
    summary: `<h4>ROSCO design model vs the full model</h4>
      <div class="eq">ω̇ = A ω + B<sub>β</sub> β,&nbsp;&nbsp; K<sub>p</sub> = (2ζω<sub>n</sub> + A)/B,&nbsp;&nbsp; K<sub>i</sub> = ω<sub>n</sub>²/B</div>
      A and B come from the linearisation (rotor-speed mode and its residue) and from the Cp table. The ω<sub>n</sub>, ζ implied by the DISCON gains are compared with what the full closed loop achieves.`,
  };
  const PG = {};
  ['pointerdown', 'wheel'].forEach(ev => $('#progress').addEventListener(ev, () => { PG.hold = true; }, { passive: true }));
  $('#btnLog').onclick = () => {
    showScreen('progress'); $('#btnLog').hidden = true; $('#progDone').hidden = false; $('#progHold').textContent = '';
    $('#progMsg').textContent = 'Finished · click a step to see its maths'; PG.hold = true;
    $('#progOpen').textContent = 'Back to results';
    $('#progOpen').onclick = () => showScreen('results');
    if (PG.charts) PG.charts.forEach(c => c.render());
  };
  function startProgress(cfg) {
    Object.assign(PG, { n: 0, stage: null, t: {}, last: {}, charts: null, per: [], modes: [], cl: [], hold: false, opened: false, shown: false, summary: null,
      closed: cfg.mode !== 'open' && !!cfg.discon });
    $('#progDone').hidden = true; $('#progOpen').textContent = 'Open results';
    $('#progTitle').textContent = 'Analysing ' + (cfg.lin_dir.split(/[\\/]/).filter(Boolean).pop() || '');
    $('#progLog').innerHTML = ''; $('#logMeta').textContent = '';
    $('#progMath').innerHTML = MATH.scan; $('#mathTitle').textContent = 'What is being computed';
    const ol = $('#progSteps'); ol.innerHTML = '';
    STAGES.forEach(([k, lab]) => {
      const li = h('li', PG.closed || k !== 'closed' ? '' : 'skip', ol); li.dataset.k = k;
      h('span', 'ic', li); h('span', null, li, lab); h('span', 't', li, '');
      li.onclick = () => { if (MATH[k]) $('#progMath').innerHTML = MATH[k]; };
    });
    const mk = (id, o) => new LB.Chart($(id), Object.assign({ height: 210, xlabel: 'Wind speed [m/s]', xname: 'Wind', xunit: 'm/s', series: [] }, o));
    PG.charts = [
      mk('#pc1', { ylog: true, ylabel: 'Periodicity index', hover: 'nearest' }),
      mk('#pc2', { ylabel: 'f_n [Hz]', hover: 'nearest', legend: false }),
      mk('#pc3', { ylabel: 'Margin', hover: 'nearest' }),
    ];
  }
  function progressCharts() {
    const [c1, c2, c3] = PG.charts;
    c1.setData([
      { name: 'raw (before MBC)', type: 'scatter', x: PG.per.map(d => d.wind), y: PG.per.map(d => d.per_raw), color: S(2), marker: 'circle', size: 4 },
      { name: 'after MBC', type: 'scatter', x: PG.per.map(d => d.wind), y: PG.per.map(d => Math.max(d.per_mbc, 1e-12)), color: S(1), marker: 'square', size: 4 }]);
    const pts = PG.modes.flatMap(d => (d.modes || []).map(([f, z]) => ({ w: d.wind, f, z })));
    const ramp = seqRamp(11);
    c2.setData([{ name: 'modes', type: 'scatter', x: pts.map(q => q.w), y: pts.map(q => q.f), size: 3.4,
      colors: pts.map(q => (q.z < 0 ? cssv('--crit') : ramp[Math.min(10, Math.round(Math.sqrt(Math.max(q.z, 0) / 0.2) * 10))])),
      tips: pts.map(q => `${fmt(q.w, 3)} m/s · ${fmt(q.f, 3)} Hz · ζ ${fmt(q.z * 100, 3)} %`) }]);
    const cl = PG.cl;
    c3.setData([
      { name: 'GM [dB]', type: 'scatter', x: cl.map(d => d.wind), y: cl.map(d => d.gm), color: S(2), size: 4, markers: cl.map(d => (d.stable ? 'diamond' : 'x')),
        tips: cl.map(d => `${fmt(d.wind, 3)} m/s · GM ${fmt(d.gm, 3)} dB · ${d.stable ? 'stable' : 'UNSTABLE'}`) },
      { name: 'PM [deg]', type: 'scatter', x: cl.map(d => d.wind), y: cl.map(d => d.pm), color: S(3), size: 4, markers: cl.map(d => (d.stable ? 'circle' : 'x')),
        tips: cl.map(d => `${fmt(d.wind, 3)} m/s · PM ${fmt(d.pm, 3)}° · s_m ${fmt(d.sm, 3)}`) }]);
  }
  function progressUpdate(p) {
    const box = $('#progLog'), atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 8;
    (p.log || []).forEach(l => {
      const d = h('div', null, box);
      h('span', 't', d, `[${l.t.toFixed(1).padStart(5)} s] `);
      h('span', 'st', d, l.stage);
      const tx = h('span', /UNSTABLE|^WARNING/.test(l.text) ? 'bad' : null, d, ' ' + l.text);
      void tx;
      const D = l.data || {};
      PG.last = PG.last || {};
      PG.last[l.stage] = l.t;                                   // step durations from the log timestamps
      if (l.stage === 'read' && D.per_raw !== undefined) PG.per.push(D);
      if (l.stage === 'modes' && D.wind !== undefined) PG.modes.push(D);
      if (l.stage === 'closed' && D.stable !== undefined) PG.cl.push(D);
    });
    if ((p.log || []).length) { if (atEnd) box.scrollTop = box.scrollHeight; progressCharts(); }
    PG.n = p.log_n || PG.n;
    $('#logMeta').textContent = `${PG.n} entries`;
    // steps
    const order = STAGES.map(s => s[0]), cur = p.done && !p.error ? 'done' : p.stage;
    if (cur !== PG.stage) {
      if (PG.stage && PG.t[PG.stage] && PG.t[PG.stage].end === undefined) PG.t[PG.stage].end = p.elapsed;
      if (!PG.t[cur]) PG.t[cur] = { start: p.elapsed };
      PG.stage = cur;
      if (MATH[cur]) $('#progMath').innerHTML = MATH[cur];
    }
    const ci = cur === 'done' ? order.length : order.indexOf(cur);
    let prevEnd = 0;
    $$('#progSteps li').forEach((li, i) => {
      const k = li.dataset.k, skip = li.classList.contains('skip');
      li.classList.toggle('done', !skip && (i < ci));
      li.classList.toggle('active', !skip && i === ci && !p.error);
      const tEl = li.querySelector('.t'), last = (PG.last || {})[k];
      if (!skip && last !== undefined) {
        const end = i === ci ? (p.elapsed || last) : last;
        tEl.textContent = fmt(Math.max(0, end - prevEnd), 2) + ' s';
        prevEnd = last;
      }
      if (i < ci || skip) li.querySelector('.ic').textContent = skip ? '–' : '✓';
    });
    const el = p.elapsed || 0, pct = p.pct || 0;
    $('#progTime').textContent = `${fmt(el, 3)} s` + (pct > 8 && pct < 100 ? ` · about ${fmt(el * (100 - pct) / pct, 2)} s left` : '');
  }
  async function poll() {
    try {
      const p = await (await fetch('/api/progress?since=' + PG.n)).json();     // not api(): an 'error' field here is data
      $('#progBar').style.width = (p.pct || 0) + '%';
      $('#progMsg').textContent = p.msg || p.stage;
      progressUpdate(p);
      if (!p.done) sbState('busy', p.msg || p.stage || 'Analysing', p.pct || 0);
      if (p.error) {
        sbState('failed', 'Analysis failed');
        $('#progErr').hidden = false; $('#progBack').hidden = false; $('#btnRun').disabled = false;
        $('#progErr').textContent = `Failed during: ${p.failed_step || p.stage}\n${p.error}`;
        if (p.trace) { const d = h('details', null, $('#progErr')); h('summary', null, d, 'Details'); h('pre', null, d, p.trace); }
        return;
      }
      // Open-loop results are published before the closed loop is computed: show them at once and
      // keep polling; the closed-loop tab switches on when the rest arrives.
      if (p.open_done && !PG.opened) {
        PG.opened = true;
        PG.summary = await api('/api/summary');
        $('#btnRun').disabled = false;
        $('#progDone').hidden = false;
        $('#progOpen').onclick = () => { PG.shown = true; openResults(PG.summary); };
        // open automatically unless the user is reading this page (clicked or scrolled in it)
        if (PG.hold) $('#progHold').textContent = p.done ? 'Finished. ' : 'Open-loop results ready. ';
        else { PG.shown = true; openResults(PG.summary); }
      }
      if (PG.shown && !p.done && PG.summary && PG.summary.closed_pending) navPending(`${PG.cl.length}/${PG.summary.ops.length}`);
      if (p.done) {
        navPending(null);
        const s = await api('/api/summary');
        const wasPending = PG.summary && PG.summary.closed_pending;
        PG.summary = s;
        if (PG.hold && !PG.shown) { $('#progHold').textContent = 'Finished. '; $('#progMsg').textContent = 'Done'; }
        if (PG.shown && wasPending) refreshSummary(s);
        sbState('ready', 'Ready'); sbResults(s);
        return;
      }
    } catch (e) { $('#progErr').hidden = false; $('#progErr').textContent = e.message; $('#progBack').hidden = false; return; }
    setTimeout(poll, 300);
  }
  $('#btnNew').onclick = showSetup;

  // ================================================================== SETTINGS
  // One object in localStorage ('lb.prefs'). Look and accessibility become data-* attributes on <html>
  // (app.css), plot options go to the chart library (LB.setPrefs), the unit goes to st.freq.
  const PREF_DEF = { freq: 'rad', theme: 'light', palette: 'matlab', contrast: 'normal', motion: 'normal', density: 'normal', textsize: '1',
    lineScale: '1', grid: 'on', legend: 'auto', legendMax: '6', crosshair: 'xy', tools: 'hover', nav: 'full', insp: 'open', plotH: '1.3' };
  const prefs = (() => {
    let p = {}; try { p = JSON.parse(localGet('lb.prefs', '{}')) || {}; } catch (e) { p = {}; }
    if (!p.freq) p.freq = localGet('lb.freq', 'rad');               // the unit setting of earlier versions
    return Object.assign({}, PREF_DEF, p);
  })();
  const darkQuery = window.matchMedia ? matchMedia('(prefers-color-scheme: dark)') : null;
  function applyPrefs(redraw) {
    const r = document.documentElement.dataset;
    r.theme = prefs.theme === 'dark' || (prefs.theme === 'system' && darkQuery && darkQuery.matches) ? 'dark' : 'light';
    r.palette = prefs.palette; r.contrast = prefs.contrast; r.motion = prefs.motion; r.density = prefs.density; r.tools = prefs.tools;
    $('#workspace').style.zoom = prefs.textsize === '1' ? '' : prefs.textsize;
    document.body.classList.toggle('nav-min', prefs.nav === 'min');
    document.body.classList.toggle('insp-off', prefs.insp !== 'open');
    $('#btnInsp').classList.toggle('on', prefs.insp === 'open');
    const freqChanged = st.freq !== prefs.freq, sizeChanged = LB.prefs.heightScale !== +prefs.plotH;
    st.freq = prefs.freq;
    LB.setPrefs({ lineScale: +prefs.lineScale, grid: prefs.grid, legend: prefs.legend, legendMax: +prefs.legendMax, crosshair: prefs.crosshair, heightScale: +prefs.plotH });
    if (redraw && (freqChanged || sizeChanged) && st.summary && !$('#results').hidden) renderPanel();
  }
  function setPref(k, v) {
    prefs[k] = v;
    const out = {}; for (const key in prefs) if (prefs[key] !== PREF_DEF[key]) out[key] = prefs[key];
    localSet('lb.prefs', JSON.stringify(out));
    applyPrefs(true);
  }
  if (darkQuery && darkQuery.addEventListener) darkQuery.addEventListener('change', () => { if (prefs.theme === 'system') applyPrefs(true); });

  const SETTINGS = [
    ['Units', [
      ['freq', 'Frequency axis', 'Control plots (Bode, loop shape, sweeps, controller); Campbell diagrams are always in Hz', [['rad', 'rad/s'], ['hz', 'Hz']]],
    ]],
    ['Appearance', [
      ['theme', 'Theme', 'System follows the Windows setting', [['light', 'Light'], ['dark', 'Dark'], ['system', 'System']]],
      ['density', 'Density', 'Spacing between and inside the cards', [['compact', 'Compact'], ['normal', 'Normal'], ['comfortable', 'Roomy']]],
      ['textsize', 'Text and plot size', 'Scales the workspace (plots included)', [['0.9', '90 %'], ['1', '100 %'], ['1.1', '110 %'], ['1.25', '125 %']]],
      ['nav', 'View list', 'Full names or icons only (Ctrl+B)', [['full', 'Names'], ['min', 'Icons']]],
    ]],
    ['Plots', [
      ['legend', 'Legend', 'Auto: inside the axes, folded to “N curves” when there are more than the limit below. Beside: a column right of the plot',
        [['auto', 'Auto'], ['inside', 'Inside'], ['side', 'Beside'], ['top', 'Above'], ['off', 'Off']]],
      ['legendMax', 'Fold legends above', 'Number of curves before a legend is folded (Auto) or gets a scroll list', [['4', '4'], ['6', '6'], ['10', '10'], ['99', 'never']]],
      ['plotH', 'Plot height', 'Height of the plots in the views (a view with one plot always fills the window)', [['1', 'Compact'], ['1.3', 'Normal'], ['1.6', 'Tall']]],
      ['lineScale', 'Line width', '', [['0.75', 'Thin'], ['1', 'Normal'], ['1.5', 'Bold']]],
      ['grid', 'Grid', 'Fine adds minor lines on linear axes', [['off', 'Off'], ['on', 'On'], ['fine', 'Fine']]],
      ['crosshair', 'Crosshair', 'Full: dotted lines in both axes with the values on the axes and where each curve meets the cursor; plots of the same quantity follow the cursor together',
        [['xy', 'Full'], ['x', 'Vertical'], ['off', 'Off']]],
      ['tools', 'Plot tools', 'Zoom, reset and export buttons: on hover over a plot, or always in its title row', [['hover', 'On hover'], ['always', 'Always']]],
    ]],
    ['Accessibility', [
      ['palette', 'Curve colours', 'Colour-blind safe uses the Okabe–Ito set', [['matlab', 'MATLAB'], ['cb', 'Colour-blind safe'], ['vivid', 'Vivid']]],
      ['contrast', 'Contrast', 'Darker text, borders and plot frames', [['normal', 'Normal'], ['high', 'High']]],
      ['motion', 'Motion', 'Turns animations off', [['normal', 'Normal'], ['reduce', 'Reduced']]],
    ]],
  ];
  /** A dialog in the shared modal: title, body builder, optional footer. Esc or a click outside closes it. */
  function dialog(title, build, foot) {
    const mc = $('#modalCard');
    mc.className = 'modal-card dlg'; mc.innerHTML = '';
    const head = h('div', 'modal-head', mc);
    h('h2', null, head, title); h('span', 'grow', head);
    const x = h('button', 'ib', head); x.title = 'Close (Esc)'; x.innerHTML = '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>';
    const body = h('div', 'dlg-body', mc);
    build(body);
    if (foot) foot(h('div', 'dlg-foot', mc));
    $('#modal').hidden = false;
    const done = () => { $('#modal').hidden = true; mc.innerHTML = ''; mc.className = 'modal-card'; document.removeEventListener('keydown', esc); $('#modal').onclick = null; };
    const esc = (e) => { if (e.key === 'Escape') done(); };
    x.onclick = done;
    $('#modal').onclick = (e) => { if (e.target === $('#modal')) done(); };
    document.addEventListener('keydown', esc);
    return done;
  }
  function openSettings() {
    dialog('Settings', (body) => {
      const swatch = (row) => { const sw = h('span', 'swatches', row); for (let k = 1; k <= 8; k++) h('i', null, sw).style.background = S(k); return sw; };
      SETTINGS.forEach(([sec, rows]) => {
        h('div', 'set-sec', body, sec);
        rows.forEach(([k, t, d, opts]) => {
          const row = h('div', 'set-row', body), l = h('div', null, row);
          const tt = h('div', 't', l, t);
          if (d) h('div', 'd', l, d);
          const seg = h('div', 'seg', row);
          let sw = k === 'palette' ? swatch(tt) : null;
          opts.forEach(([v, lab]) => {
            const b = h('button', prefs[k] === v ? 'on' : '', seg, lab);
            b.onclick = () => {
              setPref(k, v); $$('button', seg).forEach(x => x.classList.toggle('on', x === b));
              if (sw) { sw.remove(); sw = swatch(tt); }
            };
          });
        });
      });
    }, (f) => {
      h('span', 'muted', f, 'Saved on this PC for LinBench.');
      const r = h('button', 'btn', f, 'Restore defaults');
      r.onclick = () => { Object.keys(PREF_DEF).forEach(k => { prefs[k] = PREF_DEF[k]; }); localSet('lb.prefs', '{}'); applyPrefs(true); openSettings(); };
    });
  }
  function openShortcuts() {
    dialog('Keyboard shortcuts', (body) => {
      const g = h('div', 'keys', body);
      [['Ctrl K', 'Find a view, a wind speed or an action'], ['← / →', 'Previous / next wind speed'], ['Ctrl ,', 'Settings'], ['Ctrl B', 'View list: names or icons'],
        ['Esc', 'Close a dialog or an enlarged plot'], ['?', 'This list'],
        ['Drag', 'Pan a plot'], ['Shift + drag', 'Zoom box (thin box: one axis)'], ['Ctrl + wheel', 'Zoom about the pointer'], ['Double-click', 'Reset the view'],
        ['Legend: click', 'Hide / show a curve'], ['Legend: double-click', 'Only this curve (again: all)'], ['Legend: hover', 'Highlight a curve']]
        .forEach(([k, t]) => { h('kbd', null, g, k); h('span', null, g, t); });
    });
  }
  $('#btnSettings').onclick = openSettings;
  $('#mKeys').onclick = () => { closeMenu(); openShortcuts(); };
  $('#mGuide').onclick = () => { closeMenu(); window.open('guide.html', '_blank'); };
  $('#btnMenu').onclick = (e) => { e.stopPropagation(); $('#menu').hidden = !$('#menu').hidden; };
  function closeMenu() { $('#menu').hidden = true; }
  document.addEventListener('click', (e) => { if (!$('#menu').hidden && !e.target.closest('.menu-wrap')) closeMenu(); });
  $$('#menu button').forEach(b => b.addEventListener('click', closeMenu));
  $('#btnNav').onclick = () => { setPref('nav', prefs.nav === 'min' ? 'full' : 'min'); renderNav(); };
  $('#btnInsp').onclick = () => { setPref('insp', prefs.insp === 'open' ? 'closed' : 'open'); if (st.summary && !$('#results').hidden) renderPanel(); };
  applyPrefs(false);

  // ================================================================== RESULTS
  function openResults(s) {
    st.summary = s; st.cache = {};
    st.op = defaultOp(s);
    const d = s.defaults;
    st.io = { input: d.input, output: d.output };
    st.tio = { input: d.input, output: d.output, kind: 'step', amp: null, T: '' };
    st.cio = { input: d.wind_input !== null ? d.wind_input : d.input, output: d.output, kind: 'step', amp: 1, T: '', out_kind: 'plant' };
    const si = s.std_inputs, so = s.std_outputs, has = (v) => v !== null && v !== undefined;
    st.sio = { mode: 'set', ins: [si.pitch, si.torque].filter(has), outs: [so.genspeed, so.ptfmpitch, so.nacacc_fa].filter(has) };
    if (!st.sio.ins.length) st.sio.ins = [d.input];
    if (!st.sio.outs.length) st.sio.outs = [d.output];
    st.gs = { pair: 'pi', range: '0.25-4', n: 25, color: 'sp', target: 0.7, trial: {} };
    renderInfo();
    renderChips();
    if (!clAvailable(s)) st.tab = 'ol';
    if (s.mode === 'closed' && clAvailable(s)) st.tab = 'cl';
    showScreen('results');
    setView(st.tab);
    sbResults(s);
  }
  const clAvailable = (s) => !!(s.has_controller && s.mode !== 'open' && s.closed && s.closed.length && !s.closed_pending);
  /** The closed-loop part arrived after the results were already on screen. */
  function refreshSummary(s) {
    st.summary = s;
    renderInfo();
    renderChips();
    if (s.mode === 'closed' && clAvailable(s)) setView('cl');
    else { renderNav(); if (st.tab === 'ol' && st.sub.ol === 'campbell') renderPanel(); }    // its closed-loop switch becomes available
  }
  function defaultOp(s) {
    const i = s.ops.findIndex(o => o.wind >= 17 - 1e-6);
    return i >= 0 ? i : Math.floor(s.ops.length / 2);
  }

  // ------------------------------------------------------------------ views: the list on the left
  // icon paths (24×24, stroked) for each view
  const IC = {
    schedule: 'M4 19V5M4 19h16M7 15l4-5 3 3 5-7', campbell: 'M4 20V4M4 20h16M6 17c4-1 8-3 13-9M6 12h13M6 8c5 1 9 4 13 8',
    modes: 'M3 12c2-5 4-5 6 0s4 5 6 0 4-5 6 0', tf: 'M4 8h4l3-3 3 6 2-2h4M4 17h16M4 14l5 2 4-3 7 2', time: 'M3 15h4l2-8 3 12 2-6 2 2h5',
    sweep: 'M4 20V4M4 20h16M7 16l3-3 3 2 6-7M7 11l4 1 3-4 5 1', struct: 'M5 5h5v5H5zM14 5h5v5h-5zM5 14h5v5H5zM14 14h5v5h-5z',
    summary: 'M12 3l8 3v6c0 4.5-3.5 7.5-8 9-4.5-1.5-8-4.5-8-9V6zM8.5 12l2.5 2.5 4.5-5', loop: 'M3 7c4 0 6 2 8 6s4 5 10 5M3 18h18',
    nyquist: 'M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16M4 12h16M12 4v16', poles: 'M4 12h16M12 4v16M7 7l3 3M10 7l-3 3M15 15l3 3M18 15l-3 3',
    gains: 'M4 4h16v16H4zM4 14c4-1 6-6 10-7s5 2 6 4', cltime: 'M3 17c3 0 3-10 6-10s3 8 6 8 3-3 6-3', ctrl: 'M4 7h4M12 7h8M4 17h10M18 17h2M10 5v4M16 15v4',
  };
  const SUBS = {
    ol: [['schedule', 'Operating points'], ['campbell', 'Campbell diagram'], ['modes', 'Modes'], ['tf', 'Transfer function'], ['time', 'Time response'], ['sweep', 'Across wind speeds'], ['struct', 'Controllability & Hankel']],
    cl: [['summary', 'Stability summary'], ['loop', 'Loop shape'], ['nyquist', 'Nyquist'], ['poles', 'Poles & root locus'], ['gains', 'Gain study'], ['cltime', 'Time response'], ['ctrl', 'Controller & design']],
  };
  const GROUP = { ol: 'Open loop', cl: 'Closed loop' };
  const icon = (k) => `<svg viewBox="0 0 24 24"><path d="${IC[k] || ''}"/></svg>`;
  let navPend = null;
  function navPending(txt) { navPend = txt; if (st.summary && !$('#results').hidden) renderNav(); }
  function renderNav() {
    const nav = $('#sidenav'), s = st.summary; nav.innerHTML = '';
    if (!s) return;
    const clOk = clAvailable(s);
    ['ol', 'cl'].forEach(t => {
      const sec = h('div', 'nav-sec', nav, GROUP[t]);
      if (t === 'cl' && !clOk && navPend) h('span', 'cnt', sec, navPend + '…');
      SUBS[t].forEach(([k, lab]) => {
        const b = h('button', 'nav-it' + (st.tab === t && st.sub[t] === k ? ' on' : ''), nav);
        b.innerHTML = icon(k); h('span', null, b, lab);
        b.title = lab;
        if (t === 'cl' && !clOk) { b.disabled = true; b.title = s.closed_pending ? 'The closed loop is still being computed; these views switch on when it is ready' : 'Needs a DISCON.IN, and Closed loop or Both chosen at the start'; }
        b.onclick = () => setView(t, k);
      });
      if (t === 'cl' && !clOk && !s.closed_pending) h('div', 'nav-note', nav, 'Give a DISCON.IN and choose Closed loop or Both to use these views.');
    });
    const foot = h('div', 'nav-foot', nav);
    const fb = h('button', 'nav-it', foot);
    fb.innerHTML = prefs.nav === 'min' ? '<svg viewBox="0 0 24 24"><path d="M9 6l6 6-6 6"/></svg>' : '<svg viewBox="0 0 24 24"><path d="M15 6l-6 6 6 6"/></svg>';
    h('span', null, fb, 'Icons only'); fb.title = prefs.nav === 'min' ? 'Show the view names (Ctrl+B)' : 'Icons only (Ctrl+B)';
    fb.onclick = () => { setPref('nav', prefs.nav === 'min' ? 'full' : 'min'); renderNav(); };
  }
  /** Open a view: group 'ol' | 'cl' and (optionally) one of its views. */
  function setView(t, k) {
    if (t === 'cl' && st.summary && !clAvailable(st.summary)) return;
    st.tab = t;
    if (k) { st.sub[t] = k; localSet('lb.sub', JSON.stringify(st.sub)); }
    if (!SUBS[t].some(x => x[0] === st.sub[t])) st.sub[t] = SUBS[t][0][0];
    renderNav();
    const lab = SUBS[t].find(x => x[0] === st.sub[t])[1];
    document.title = lab + ' · LinBench' + (st.summary && st.summary.info.name ? ' · ' + st.summary.info.name : '');
    $('#workspace').scrollTop = 0;
    renderPanel();
  }

  // ------------------------------------------------------------------ turbine & model (dialog from the top bar)
  $('#hdrTurbine').onclick = () => {
    const s = st.summary; if (!s) return;
    dialog(s.info.name || 'Turbine & model', (body) => { body.appendChild($('#infoKpis').cloneNode(true)); body.appendChild($('#infoGrid').cloneNode(true)); });
    $('#modalCard').classList.add('spec');
  };
  function renderInfo() {
    const s = st.summary, I = s.info;
    const hd = $('#hdrTurbine'); hd.innerHTML = '';
    const cl = (s.closed || []).filter(r => !r.error), un = cl.filter(r => !r.stable);
    const dot = h('i', 'dot', hd);
    dot.style.background = s.closed_pending || !cl.length ? cssv('--muted') : un.length ? cssv('--crit') : cssv('--good');
    h('b', null, hd, I.name || 'Turbine & model');
    h('span', null, hd, `${I.n_ops} points · ${fmt(I.wind_range[0], 3)}–${fmt(I.wind_range[1], 3)} m/s`);
    // ---- specification sheet (dialog): headline figures, then sections of label / value rows with their source
    const tp = I.turbine_params || {}, src = I.turbine_sources || {}, ops = s.ops;
    const k = $('#infoKpis'); k.innerHTML = ''; k.className = 'spec-kpis';
    const kpi = (lab, v, unit, sub) => {
      if (v === null || v === undefined || v === '') return;
      const t = h('div', 'spec-kpi', k); h('span', 'k', t, lab);
      const vv = h('span', 'v tnum', t, String(v)); if (unit) h('small', null, vv, unit);
      if (sub) h('span', 's', t, sub);
    };
    const ratedMW = tp.rated_power ? tp.rated_power / 1e6 : I.rated_power_kw ? I.rated_power_kw / 1000 : null;
    const rpm = (w) => w * 60 / TWO_PI;
    kpi('Rated power', ratedMW === null ? null : fmt(ratedMW, 3), 'MW');
    kpi('Rotor diameter', tp.R ? fmt(2 * tp.R, 4) : null, 'm');
    kpi('Rated wind', tp.v_rated ? fmt(tp.v_rated, 3) : null, 'm/s');
    kpi('Rated rotor speed', tp.rated_rotor_speed ? fmt(rpm(tp.rated_rotor_speed), 4) : null, 'rpm');
    kpi('Operating points', I.n_ops, '', `${fmt(I.wind_range[0], 3)}–${fmt(I.wind_range[1], 3)} m/s`);
    kpi('Linear model', I.n_states, 'states', I.has_blade_dofs ? 'with blade DOFs' : 'no blade DOFs');
    kpi('Controller', s.has_controller ? 'ROSCO' : 'none', '', s.has_controller ? 'from DISCON.IN' : 'open loop only');
    const g = $('#infoGrid'); g.innerHTML = ''; g.className = 'spec-grid';
    if (!I.has_blade_dofs) h('div', 'banner', g, 'No blade degrees of freedom in these linearisations (ElastoDyn FlapDOF / EdgeDOF off): flapwise / edgewise, collective and whirl modes cannot appear.');
    if (I.name_order_differs) h('div', 'banner', g, 'File names are not in wind-speed / azimuth order; LinBench orders operating points by the values written inside each file.');
    let box = null;
    const sec = (t) => { const d = h('section', 'spec-sec', g); h('h4', null, d, t); box = h('div', 'spec-rows', d); return d; };
    // one row; opts.long: full width and wrapped (paths, lists), opts.src: where the value came from, opts.code: raw parameter
    const row = (lab, val, opts) => {
      opts = opts || {};
      if (val === null || val === undefined || val === '') return;
      const r = h('div', 'spec-row' + (opts.long ? ' long' : ''), box);
      const l = h('span', 'l', r, lab); if (opts.code) h('code', null, l, opts.code);
      const v = h('span', 'v' + (opts.mono ? ' mono' : ''), r);
      if (Array.isArray(val)) val.forEach(x => h('span', 'chip-s', v, x)); else v.textContent = String(val);
      if (opts.src) h('span', 'src', r, opts.src);
    };
    const S_ = (key) => (src[key] ? src[key].replace(/^.*[\\/]/, '') : '');
    sec('Turbine');
    if (tp.R) row('Rotor radius / diameter', `${fmt(tp.R, 4)} m / ${fmt(2 * tp.R, 4)} m`, { src: S_('R') });
    if (ratedMW !== null) row('Rated power', `${fmt(ratedMW, 4)} MW`, { src: tp.rated_power ? S_('rated_power') : 'linearisation (max)' });
    if (tp.rated_rotor_speed) row('Rated rotor speed', `${fmt(tp.rated_rotor_speed, 4)} rad/s · ${fmt(rpm(tp.rated_rotor_speed), 4)} rpm`, { src: S_('rated_rotor_speed') });
    if (tp.rated_rotor_speed && tp.Ng) row('Rated generator speed', `${fmt(rpm(tp.rated_rotor_speed) * tp.Ng, 5)} rpm`, { src: 'rotor speed × gearbox' });
    if (tp.v_rated) row('Rated wind speed', `${fmt(tp.v_rated, 3)} m/s`, { src: S_('v_rated') });
    if (tp.Ng) row('Gearbox ratio', fmt(tp.Ng, 4), { src: S_('Ng') });
    if (tp.J) row('Drivetrain inertia (rotor side)', `${fmt(tp.J, 4)} kg·m²`, { src: S_('J') });
    if (tp.rho) row('Air density', `${fmt(tp.rho, 4)} kg/m³`, { src: S_('rho') });
    if (tp.GenEff) row('Generator efficiency', `${fmt(tp.GenEff, 4)} %`, { src: S_('GenEff') });
    sec('Operating range (from the linearisations)');
    const rng = (f, d) => { const v = ops.map(f).filter(x => x !== null && x !== undefined && isFinite(x)); return v.length ? `${fmt(Math.min(...v), d)} – ${fmt(Math.max(...v), d)}` : null; };
    row('Wind speeds', ops.map(o => fmt(o.wind, 3)), { long: true });
    row('Rotor speed', rng(o => o.rotor_rpm, 4) + ' rpm');
    row('Blade pitch', rng(o => o.pitch_deg, 3) + ' °');
    const pw = rng(o => (o.power_kw === null ? null : o.power_kw / 1000), 3); if (pw) row('Power', pw + ' MW');
    row('Files', `${I.n_files} (${[...new Set(ops.map(o => o.n_az))].join(' / ')} azimuths per wind speed)`);
    sec('Linear model');
    row('Title', I.title, { long: true });
    row('States / inputs / outputs', `${I.n_states} / ${I.n_inputs} / ${I.n_outputs}`);
    row('Rotor triplets (MBC)', I.rotor_triplets);
    row('Blade DOFs', I.has_blade_dofs ? 'yes' : 'no');
    row('Removed states', (I.removed_states || []).map(x => x.replace(/\s*\(internal.*$/, '')).join(', ') || 'none');
    row('Structural DOFs', I.dofs, { long: true });
    row('Modules', String(I.modules || '').split(/;\s*/).filter(Boolean), { long: true });
    if (s.controller) {
      const c = s.controller;
      sec('ROSCO controller');
      const RS = {
        PC_ControlMode: ['Pitch control', v => ({ 0: 'off (fixed pitch)', 1: 'PI, gain-scheduled on pitch' })[v]],
        VS_ControlMode: ['Torque control', v => ({ 0: 'off', 1: 'Kω² with PI transitions', 2: 'TSR tracking, PI (wind-speed estimate)', 3: 'TSR tracking, PI (power based)' })[v]],
        VS_ConstPower: ['Above rated', v => ({ 0: 'constant torque', 1: 'constant power' })[v]],
        PC_RefSpd: ['Generator-speed set point', v => `${fmt(v, 5)} rad/s · ${fmt(rpm(v), 5)} rpm`],
        VS_RtTq: ['Rated generator torque', v => `${fmt(v / 1e3, 5)} kN·m`],
        VS_RtPwr: ['Rated power (DISCON)', v => `${fmt(v / 1e6, 4)} MW`],
        F_LPFType: ['Generator-speed filter', v => ({ 1: '1st-order low-pass', 2: '2nd-order low-pass' })[v]],
        F_LPFCornerFreq: ['Filter corner', v => `${fmt(v, 4)} rad/s · ${fmt(v / TWO_PI, 3)} Hz`],
        F_LPFDamping: ['Filter damping', v => fmt(v, 3)],
        PA_Mode: ['Pitch actuator', v => ({ 0: 'ideal (not modelled)', 1: '1st order', 2: '2nd order' })[v]],
        PA_CornerFreq: ['Actuator corner', v => `${fmt(v, 4)} rad/s · ${fmt(v / TWO_PI, 3)} Hz`],
        PA_Damping: ['Actuator damping', v => fmt(v, 3)],
        Fl_Mode: ['Floating feedback', v => (+v === 0 ? 'off' : `on (mode ${v})`)],
        F_FlCornerFreq: ['Floating feedback filter', v => (Array.isArray(v) ? `${fmt(v[0], 4)} rad/s, ζ ${fmt(v[1], 3)}` : `${fmt(v, 4)} rad/s`)],
        F_FlHighPassFreq: ['Floating feedback high-pass', v => `${fmt(v, 4)} rad/s`],
        PS_Mode: ['Peak shaving', v => (+v === 0 ? 'off' : `on (mode ${v})`)],
        WE_Mode: ['Wind-speed estimator', v => ({ 0: 'none (filtered hub wind)', 1: 'Immersion & Invariance', 2: 'Extended Kalman filter' })[v]],
      };
      const set = new Map(c.settings);
      Object.keys(RS).forEach(key => { if (!set.has(key)) return; const v = set.get(key); let t = null; try { t = RS[key][1](v); } catch (e) { t = null; } row(RS[key][0], t === undefined || t === null ? (Array.isArray(v) ? v.join(', ') : v) : t, { code: key }); });
      c.settings.forEach(([a, b]) => { if (!RS[a]) row(a, Array.isArray(b) ? b.join(', ') : b, { code: a }); });
      const rg = (a, d) => (a && a.length ? (a.length === 1 ? fmt(a[0], d) : `${fmt(Math.min(...a), d)} … ${fmt(Math.max(...a), d)}`) : null);
      if (c.PC_GS_angles && c.PC_GS_angles.length) {
        row('Pitch gain schedule', `${c.PC_GS_angles.length} points, pitch ${rg(c.PC_GS_angles.map(deg), 3)}°`, { code: 'PC_GS_angles' });
        row('Pitch Kp', rg(c.PC_GS_KP, 4) + ' s', { code: 'PC_GS_KP' });
        row('Pitch Ki', rg(c.PC_GS_KI, 4), { code: 'PC_GS_KI' });
      }
      if (c.VS_KP && c.VS_KP.length) row('Torque Kp / Ki', `${rg(c.VS_KP, 4)} / ${rg(c.VS_KI, 4)}`, { code: 'VS_KP · VS_KI' });
      if (c.Fl_Kp && c.Fl_Kp.length && c.Fl_Kp.some(x => x)) row('Floating feedback gain', rg(c.Fl_Kp, 4), { code: 'Fl_Kp' });
      row('Generator-speed notches', c.gs_notches.length ? c.gs_notches.map(n => `${fmt(n[0], 4)} rad/s (${fmt(n[0] / TWO_PI, 3)} Hz)`) : 'none', { long: c.gs_notches.length > 0 });
      row('Tower-top notches', c.tt_notches.length ? c.tt_notches.map(n => `${fmt(n[0], 4)} rad/s (${fmt(n[0] / TWO_PI, 3)} Hz)`) : 'none', { long: c.tt_notches.length > 0 });
    }
    sec('Input files');
    row('Linearisation folder', I.lin_dir, { long: true, mono: true });
    row('DISCON', I.discon || 'not given', { long: !!I.discon, mono: !!I.discon });
    if (I.discon_header) row('DISCON header', I.discon_header, { long: true });
    row('Tuning YAML', I.yaml || 'not given', { long: !!I.yaml, mono: !!I.yaml });
    row('Cp-Ct-Cq table', I.cp || 'not given', { long: !!I.cp, mono: !!I.cp });
    row('OpenFAST model', I.fst || 'not given', { long: !!I.fst, mono: !!I.fst });
    if ((I.scan_warnings || []).length) { sec('Warnings'); I.scan_warnings.forEach(w => row('⚠', w, { long: true })); }
  }

  function clRow(i) { return (st.summary.closed || []).find(c => c.op === i); }
  function renderChips() {
    const s = st.summary, box = $('#opChips'); box.innerHTML = '';
    s.ops.forEach((o, i) => {
      const c = h('button', 'chip' + (i === st.op ? ' on' : ''), box);
      c.type = 'button';
      const r = clRow(i);
      if (r && r.stable !== undefined) { const dot = h('i', null, c); dot.style.background = r.stable ? cssv('--good') : cssv('--crit'); dot.title = r.stable ? 'closed loop stable' : 'closed loop unstable'; }
      h('span', null, c, fmt(o.wind, 3));
      c.title = `${fmt(o.wind, 4)} m/s · ${fmt(o.rotor_rpm, 4)} rpm` + (r && r.region_name ? ' · ' + r.region_name : '');
      c.onclick = () => gotoOp(i);
    });
    const on = $('.chip.on', box);
    if (on) { const l = on.offsetLeft - box.offsetLeft, r = l + on.offsetWidth; if (l < box.scrollLeft || r > box.scrollLeft + box.clientWidth) box.scrollLeft = l - box.clientWidth / 2; }
    $('#opPrev').disabled = st.op <= 0; $('#opNext').disabled = st.op >= s.ops.length - 1;
    renderStrip();
  }
  function gotoOp(i) {
    if (!st.summary || i < 0 || i >= st.summary.ops.length || i === st.op) return;
    st.op = i; renderChips(); renderPanel();
  }
  $('#opPrev').onclick = () => gotoOp(st.op - 1);
  $('#opNext').onclick = () => gotoOp(st.op + 1);
  function renderStrip() {
    const s = st.summary, o = s.ops[st.op], r = clRow(st.op), d = (s.design || [])[st.op] || {};
    const S_ = $('#opStrip'); S_.innerHTML = '';
    // one block per value: small label above, value with unit below; values that are not known are left out
    let grp = null;
    const group = (title) => { grp = h('div', 'st-grp', S_); grp.title = title; };
    const it = (k, v, unit, tip, sig) => {
      if (v === null || v === undefined || !isFinite(v)) return;
      const b = h('div', 'st', grp); h('span', 'k', b, k);
      const vv = h('span', 'v tnum', b, fmt(v, sig || 4)); if (unit) h('small', null, vv, unit);
      if (tip) b.title = tip;
    };
    group('Operating point (trim values written in the .lin files)');
    it('Rotor', o.rotor_rpm, 'rpm');
    it('Pitch', o.pitch_deg, '°', '', 3);
    it('Gen torque', o.gen_torque !== null ? o.gen_torque / 1e6 : null, 'MN·m');
    it('Power', o.power_kw !== null ? o.power_kw / 1000 : null, 'MW');
    it('TSR', o.tsr, '');
    it('Cp', o.cp, '');
    if (r) {
      group('ROSCO controller at this wind speed (from DISCON)');
      if (r.region_name) { const b = h('div', 'st', grp); h('span', 'k', b, 'Loop'); h('span', 'reg', b, r.region_name); }
      if (r.region === 'pitch') { it('Kp', r.Kp, 's', 'pitch PI proportional gain'); it('Ki', r.Ki, '', 'pitch PI integral gain'); }
      else { it('VS Kp', r.VS_Kp, '', 'torque PI proportional gain'); it('VS Ki', r.VS_Ki, '', 'torque PI integral gain'); }
    }
    if (d.A_lin !== undefined || d.B_lin !== undefined) {
      group('1-DOF design model from the linearisation: dω/dt = A·ω + B·β');
      it('A', d.A_lin, '1/s', 'rotor-speed sensitivity A of the design model', 3);
      it('B', d.B_lin, '(rad/s)/rad', 'pitch sensitivity B of the design model', 3);
    }
  }

  // ------------------------------------------------------------------ channel selectors
  function channelSelect(parent, label, list, value, onchange, filter) {
    const f = h('label', 'fl', parent);
    h('span', 'lab', f, label);
    const sel = h('select', null, f);
    const groups = {};
    list.forEach(c => { if (filter && !filter(c)) return; (groups[c.module] = groups[c.module] || []).push(c); });
    Object.keys(groups).forEach(m => {
      const og = h('optgroup', null, sel); og.label = m;
      groups[m].forEach(c => { const op = h('option', null, og, `${c.label}  [${c.disp_unit || '-'}]`); op.value = c.i; if (c.i === value) op.selected = true; });
    });
    sel.onchange = () => onchange(+sel.value);
    return sel;
  }
  function presetButtons(parent, onPick) {
    const s = st.summary, si = s.std_inputs, so = s.std_outputs;
    const box = h('div', 'fl', parent);
    h('span', 'lab', box, 'Quick pick');
    const p = h('div', 'presets', box);
    const add = (lab, i, o) => { if (i === null || i === undefined || o === null || o === undefined) return; const b = h('button', 'btn sm', p, lab); b.onclick = () => onPick(i, o); };
    add('Pitch → Gen speed', si.pitch, so.genspeed);
    add('Torque → Gen speed', si.torque, so.genspeed);
    add('Wind → Gen speed', si.wind, so.genspeed);
    add('Pitch → Platform pitch', si.pitch, so.ptfmpitch);
    add('Wind → Platform pitch', si.wind, so.ptfmpitch);
    add('Pitch → Tower-top accel', si.pitch, so.nacacc_fa);
  }
  const inputOk = (c) => c.role !== 'cos' && c.role !== 'sin';
  const outputOk = (c) => c.role !== 'rot';

  // ================================================================== PANELS
  async function renderPanel() {
    destroyCharts();
    const host = $('#panel'); host.innerHTML = '';
    const P = h('div', null, host);          // fresh container: a slow, superseded render writes into a detached node
    renderStrip();
    if (!PANELS[st.tab + ':' + st.sub[st.tab]]) st.sub[st.tab] = SUBS[st.tab][0][0];
    const key = st.tab + ':' + st.sub[st.tab];
    const fn = PANELS[key];
    $('#inspector').innerHTML = '';
    try {
      const run = fn(P);            // the synchronous part builds the view's settings row: move it at once
      buildInspector(P);
      await run;
    } catch (e) { console.error(e); errorBox(P, e); }
    if (P.isConnected) { buildInspector(P); fitSingle(P); }
  }
  /** A view with one plot: the plot takes the height of the workspace. */
  function fitSingle(P) {
    const cs = [...P.querySelectorAll('.lb-chart')].map(e => e._lbChart).filter(Boolean);
    if (cs.length !== 1 || !P.isConnected) return;
    const c = cs[0], cardEl = c.host.closest('.card');
    if (c._baseH === undefined) c._baseH = c.opts.height;
    const other = (cardEl ? cardEl.offsetHeight : 0) - c.wrap.offsetHeight;
    const hgt = Math.floor($('#workspace').clientHeight - $('.ctxbar').offsetHeight - other - 48);
    const want = Math.max(c._baseH, hgt);
    if (Math.abs(want - c.opts.height) > 4) c.set({ height: want });
  }
  let fitTimer = 0;
  window.addEventListener('resize', () => { clearTimeout(fitTimer); fitTimer = setTimeout(() => { const P = $('#panel > div'); if (P && !$('#results').hidden) fitSingle(P); }, 150); });

  // ------------------------------------------------------------------ side panel on the right (like a properties panel)
  // View settings (the view's .ctrls rows, moved here), insights a view provides (P._insights), the results (each
  // card's numbers and notes, moved here so the middle shows plots), plot options, the list of plots, data export.
  // Sections fold; their state is remembered.
  let inspObs = null, inspTmr = 0;
  function buildInspector(P) {
    const ins = $('#inspector'); ins.innerHTML = '';
    if (inspObs) { inspObs.disconnect(); inspObs = null; }
    clearTimeout(inspTmr);                    // a pending update of the previous panel must not move things into it
    if (prefs.insp !== 'open') return;          // panel closed: settings and numbers stay with the plots
    const sec = (title, open) => {
      const d = h('details', 'ins-sec', ins); d.open = localGet('lb.ins.' + title, open === false ? '0' : '1') === '1';
      d.ontoggle = () => localSet('lb.ins.' + title, d.open ? '1' : '0');
      h('summary', 'ins-h', d, title); return d;
    };
    P._ctrls = (P._ctrls || []).concat([...P.children].filter(e => e.classList.contains('ctrls')));
    if (P._ctrls.length) { const s = sec('View settings'); P._ctrls.forEach(c => s.appendChild(c)); }
    if ((P._insights || []).length) {
      const s = sec('Insights');
      P._insights.forEach(f => { try { f(s); } catch (e) { console.error(e); } });
    }
    const resSec = sec('Results'), resBox = h('div', 'ins-res', resSec);
    const goTo = (c) => { c.scrollIntoView({ behavior: prefs.motion === 'reduce' ? 'auto' : 'smooth', block: 'start' }); c.classList.remove('flash'); void c.offsetWidth; c.classList.add('flash'); };
    // numbers and notes of every card go to Results, grouped under the card's title
    const collect = () => {
      P._groups = P._groups || new Map();
      [...P.querySelectorAll('.card')].forEach(c => {
        const t = c.querySelector(':scope > .card-h h3');
        if (!t) return;
        const own = (sel) => [...c.querySelectorAll(sel)].filter(e => e.closest('.card') === c && !e.closest('table'));
        const g = P._groups.get(c) || { title: t.textContent, items: [] };
        own('.metrics').concat(own('.notes')).forEach(e => { if (!g.items.includes(e)) g.items.push(e); });
        if (g.items.length) P._groups.set(c, g);
      });
      resBox.innerHTML = '';
      P._groups.forEach((g, c) => {
        const box = h('div', 'res-grp', resBox);
        const hd = h('button', 'res-h', box, g.title); hd.title = 'Show this in the middle';
        hd.onclick = () => { if (!c.classList.contains('is-empty')) goTo(c); };
        // numbers first; explanations folded under "Notes" so the panel reads as data, not prose
        g.items.filter(e => e.classList.contains('metrics')).forEach(e => box.appendChild(e));
        const notes = g.items.filter(e => e.classList.contains('notes'));
        if (notes.length) { const d = h('details', 'res-notes', box); h('summary', null, d, 'Notes'); notes.forEach(e => d.appendChild(e)); }
      });
      if (!P._groups.size) resSec.hidden = true; else resSec.hidden = false;
      // a card left with nothing but its title (its numbers moved here) is hidden in the middle
      [...P.querySelectorAll('.card')].forEach(c => {
        const empty = !c.querySelector('.lb-chart, table, input, select, button:not(.tool), .spinner, .empty, svg, canvas')
          && ![...c.children].some(e => !e.classList.contains('card-h') && e.textContent.trim());
        c.classList.toggle('is-empty', empty);
      });
    };
    collect();
    const s2 = sec('Plots', false);
    [['legend', 'Legend', [['auto', 'Auto'], ['side', 'Beside'], ['off', 'Off']]],
      ['crosshair', 'Cursor', [['xy', 'Full'], ['x', 'Line'], ['off', 'Off']]],
      ['grid', 'Grid', [['on', 'On'], ['fine', 'Fine'], ['off', 'Off']]],
      ['lineScale', 'Lines', [['0.75', 'Thin'], ['1', 'Normal'], ['1.5', 'Bold']]]].forEach(([k, lab, opts]) => {
      const r = h('div', 'ins-row', s2); h('span', 'ins-k', r, lab);
      const seg = h('div', 'seg small', r);
      opts.forEach(([v, l]) => { const b = h('button', prefs[k] === v ? 'on' : '', seg, l); b.onclick = () => { setPref(k, v); [...seg.children].forEach(x => x.classList.toggle('on', x === b)); }; });
    });
    const s3 = sec('In this view', false), list = h('div', 'ins-list', s3);
    const fill = () => {
      list.innerHTML = '';
      const cards = [...P.querySelectorAll('.card')].filter(c => c.querySelector(':scope > .card-h h3') && !c.classList.contains('is-empty'));
      cards.forEach(c => {
        const b = h('button', 'ins-link' + (c.classList.contains('chart-card') ? ' plot' : ''), list, c.querySelector(':scope > .card-h h3').textContent);
        b.onclick = () => goTo(c);
      });
      if (!cards.length) h('div', 'muted', list, '–');
    };
    fill();
    // cards that arrive later (results computed after the view opened) join the panel; chart redraws are ignored
    const isCard = (n) => n.nodeType === 1 && !(n instanceof SVGElement) && (n.classList.contains('card') || n.classList.contains('metrics') || n.classList.contains('notes') || !!n.querySelector('.card, .metrics, .notes'));
    inspObs = new MutationObserver((muts) => {
      if (!muts.some(m => [...m.addedNodes].some(isCard))) return;
      clearTimeout(inspTmr); inspTmr = setTimeout(() => { if (P.isConnected) { collect(); fill(); fitSingle(P); } }, 200);
    });
    inspObs.observe(P, { childList: true, subtree: true });
    const s4 = sec('Data', false);
    const b = h('button', 'btn sm', s4, 'All plot data of this view (CSV)');
    b.title = 'One CSV file with the curves of every plot in this view, plot by plot';
    b.onclick = () => {
      const parts = [...P.querySelectorAll('.lb-chart')].map(e => e._lbChart).filter(Boolean)
        .map(c => `# ===== ${c.opts.title || ''} =====\n` + c.toCSV());
      LB.download(new Blob([parts.join('\n\n')], { type: 'text/csv' }), slug(SUBS[st.tab].find(x => x[0] === st.sub[st.tab])[1] + ' ' + fmt(st.summary.ops[st.op].wind, 4) + 'mps') + '.csv');
    };
  }

  // ------------------------------------------------------------------ insights (right panel): verdict line + tiles
  /** Verdict line: level 'good' | 'warn' | 'bad', a short title and one line of detail. */
  function verdict(el, level, title, detail) {
    const v = h('div', 'ins-verdict ' + level, el);
    h('i', null, v);
    const t = h('div', null, v); h('b', null, t, title); if (detail) h('span', null, t, detail);
    return v;
  }
  /** Tiles: {k: label, v: value, unit, sub: small line, level, wide: spans both columns, go: click action}; falsy items are skipped. */
  function tiles(el, items) {
    const g = h('div', 'ins-tiles', el);
    items.filter(Boolean).forEach(it => {
      const t = h(it.go ? 'button' : 'div', 'ins-tile' + (it.level ? ' ' + it.level : '') + (it.wide ? ' wide' : ''), g);
      h('span', 'k', t, it.k);
      const v = h('span', 'v tnum', t, String(it.v)); if (it.unit) h('small', null, v, it.unit);
      if (it.sub) h('span', 's', t, it.sub);
      if (it.go) { t.onclick = it.go; t.title = 'Go to this wind speed'; }
    });
    return g;
  }
  // margin targets used everywhere in LinBench: good ≥ target, attention between, bad below the lower value
  const lvl = (v, good, bad) => (v === null || v === undefined ? '' : v >= good ? 'good' : v >= bad ? 'warn' : 'bad');

  /** Loop shape view: is the loop robust, what limits it, how fast is it. */
  function loopInsights(el, r) {
    const gm = r.gm_min ? r.gm_min.gm_db : null, pm = r.pm_min ? r.pm_min.pm : null;
    const lv = { gm: gm === null ? 'good' : lvl(gm, 6, 6), pm: lvl(pm, 45, 30), sm: lvl(r.s_m, 0.5, 0.4) };
    const ratio = [['gain margin', gm === null ? Infinity : gm / 6], ['phase margin', pm === null ? Infinity : pm / 45], ['stability margin', r.s_m / 0.5]].sort((a, b) => a[1] - b[1])[0];
    const worst = !r.stable ? 'bad' : ['bad', 'warn'].find(k => Object.values(lv).includes(k)) || 'good';
    verdict(el, worst, !r.stable ? `Closed loop unstable (${r.Z} RHP pole${r.Z > 1 ? 's' : ''})` : worst === 'good' ? 'Robust: all margins on target' : 'Stable, margins below target',
      r.stable ? `limiting: ${ratio[0]} (${fmt(ratio[1] * 100, 3)} % of target)` : 'see the Nyquist view for the encirclements');
    const dt = st.summary.dt;
    tiles(el, [
      { k: 'Gain margin', v: gm === null ? '∞' : fmt(gm, 3), unit: 'dB', sub: r.gm_min ? `@ ${fw(r.gm_min.w)} · target ≥ 6` : 'no −180° crossing', level: lv.gm },
      { k: 'Phase margin', v: pm === null ? '–' : fmt(pm, 3), unit: '°', sub: r.pm_min ? `@ ${fw(r.pm_min.w)} · target ≥ 45` : '', level: lv.pm },
      { k: 'Crossover ω_c', v: r.pm_min ? fmt(wconv(r.pm_min.w), 3) : '–', unit: r.pm_min ? wunit() : '', sub: r.pm_min ? `loop time constant ≈ ${fmt(1 / r.pm_min.w, 3)} s` : '' },
      { k: 'Peak sensitivity', v: fmt(db(r.Smax), 3), unit: 'dB', sub: `s_m = ${fmt(r.s_m, 3)} · target ≤ 6 dB`, level: lv.sm },
      { k: 'Delay margin', v: r.dm === null ? '–' : fmt(r.dm * 1000, 3), unit: 'ms', sub: dt && r.dm !== null ? `${fmt(r.dm / dt, 3)} × controller step` : 'extra delay before instability',
        level: dt && r.dm !== null ? (r.dm >= 5 * dt ? 'good' : r.dm >= 2 * dt ? 'warn' : 'bad') : '' },
      r.disk_gm_db !== null && r.disk_gm_db !== undefined && r.stable && { k: 'Disk margin', v: `±${fmt(r.disk_gm_db, 3)}`, unit: 'dB', sub: `±${fmt(r.disk_pm, 3)}° at the same time`, level: lvl(r.disk_gm_db, 6, 3) },
      { k: 'Bandwidth of T', v: r.bw_T ? fmt(wconv(r.bw_T), 3) : '–', unit: r.bw_T ? wunit() : '', sub: 'speed of set-point tracking' },
      { k: 'Peak of T', v: fmt(db(r.Tmax), 3), unit: 'dB', sub: `@ ${fw(r.w_Tmax)} · noise / overshoot`, level: lvl(-db(r.Tmax), -3, -6) },
    ]);
  }

  /** Stability summary view: verdict across all wind speeds, worst values, weakest point. */
  function summaryInsights(el, rows, dly, tau) {
    const ok = rows.filter(r => r.stable), un = rows.filter(r => !r.stable);
    const minBy = (a, f) => { const b = a.filter(r => f(r) !== null && f(r) !== undefined); return b.length ? b.reduce((x, y) => (f(y) < f(x) ? y : x)) : null; };
    const gmW = minBy(ok, r => r.gm_db), pmW = minBy(ok, r => r.pm), smW = minBy(ok, r => r.s_m), zW = minBy(ok, r => r.min_zeta), dkW = minBy(ok, r => r.disk_gm_db);
    const score = (r) => Math.min(r.gm_db === null ? Infinity : r.gm_db / 6, r.pm === null ? Infinity : r.pm / 45, r.s_m / 0.5);
    const weak = minBy(ok, score);
    const go = (r) => () => { if (r) gotoOp(r.op); };
    const nP = rows.filter(r => r.region === 'pitch').length;
    const lowest = Math.min(gmW && gmW.gm_db < 6 ? 0 : 2, pmW ? { good: 2, warn: 1, bad: 0 }[lvl(pmW.pm, 45, 30)] : 2, smW ? { good: 2, warn: 1, bad: 0 }[lvl(smW.s_m, 0.5, 0.4)] : 2);
    verdict(el, un.length ? 'bad' : lowest === 0 ? 'bad' : lowest === 1 ? 'warn' : 'good',
      un.length ? `Unstable at ${un.length} of ${rows.length} wind speeds` : `Stable at all ${rows.length} wind speeds`,
      un.length ? un.map(r => fmt(r.wind, 3)).join(', ') + ' m/s' : weak ? `weakest at ${fmt(weak.wind, 3)} m/s (${fmt(score(weak) * 100, 3)} % of target)` : '');
    tiles(el, [
      weak && { k: 'Weakest wind speed', v: fmt(weak.wind, 3), unit: 'm/s', sub: `GM ${fmt(weak.gm_db, 3)} dB · PM ${fmt(weak.pm, 3)}° · s_m ${fmt(weak.s_m, 3)}`, wide: true, go: go(weak), level: lvl(score(weak), 1, 0.7) },
      gmW && { k: 'Worst gain margin', v: fmt(gmW.gm_db, 3), unit: 'dB', sub: `@ ${fmt(gmW.wind, 3)} m/s`, level: lvl(gmW.gm_db, 6, 6), go: go(gmW) },
      pmW && { k: 'Worst phase margin', v: fmt(pmW.pm, 3), unit: '°', sub: `@ ${fmt(pmW.wind, 3)} m/s`, level: lvl(pmW.pm, 45, 30), go: go(pmW) },
      smW && { k: 'Worst s_m', v: fmt(smW.s_m, 3), sub: `@ ${fmt(smW.wind, 3)} m/s · ‖S‖∞ ${fmt(-db(smW.s_m), 3)} dB`, level: lvl(smW.s_m, 0.5, 0.4), go: go(smW) },
      dkW && { k: 'Worst disk margin', v: `±${fmt(dkW.disk_gm_db, 3)}`, unit: 'dB', sub: `±${fmt(dkW.disk_pm, 3)}° · @ ${fmt(dkW.wind, 3)} m/s`, level: lvl(dkW.disk_gm_db, 6, 3), go: go(dkW) },
      zW && { k: 'Least-damped CL mode', v: fmt(zW.min_zeta * 100, 3), unit: '%', sub: `${zW.min_zeta_label || ''} · @ ${fmt(zW.wind, 3)} m/s`, level: lvl(zW.min_zeta, 0.05, 0.02), go: go(zW), wide: true },
      { k: 'Loops', v: `${nP} / ${rows.length - nP}`, sub: 'pitch / torque control points' },
      { k: 'Nyquist check', v: rows.every(r => r.nyquist_ok) ? 'Z = N + P' : 'mismatch', sub: 'eigenvalues agree with encirclements', level: rows.every(r => r.nyquist_ok) ? 'good' : 'bad' },
      dly && { k: `With ${fmt(tau * 1000, 3)} ms delay`, v: dly.some(x => x.stable === false) ? `${dly.filter(x => x.stable === false).length} unstable` : 'stable', sub: (() => { const p = dly.filter(x => x.stable && x.pm !== null); return p.length ? `worst PM ${fmt(Math.min(...p.map(x => x.pm)), 3)}°` : ''; })(),
        level: dly.some(x => x.stable === false) ? 'bad' : 'good', wide: true },
    ]);
  }

  /** Transfer function view, right panel: G(s) in factored form, what it means for control, poles and zeros. */
  function tfInsights(el, r) {
    const m = r.metrics, u = r.units, half = (L) => L.filter(q => q.im >= -1e-9);
    // one factor per real root or conjugate pair: (s + a), (s² + 2ζωₙ s + ωₙ²), s for a root at the origin
    const factor = (q) => {
      const wn = Math.hypot(q.re, q.im);
      if (wn < 1e-9) return { txt: 's', at0: 0, wn };
      if (Math.abs(q.im) < 1e-9) return { txt: `(s ${q.re <= 0 ? '+' : '−'} ${fmt(Math.abs(q.re), 3)})`, at0: -q.re, wn };
      const b = -2 * q.re;
      return { txt: `(s² ${b >= 0 ? '+' : '−'} ${fmt(Math.abs(b), 3)}s + ${fmt(wn * wn, 3)})`, at0: wn * wn, wn };
    };
    const P_ = half(r.poles).map(factor).sort((a, b) => a.wn - b.wn), Z_ = half(r.zeros).map(factor).sort((a, b) => a.wn - b.wn);
    const prod = (F) => F.reduce((a, f) => a * f.at0, 1);
    const K = !m.integrator && m.dc_gain !== null && prod(Z_) !== 0 ? m.dc_gain * prod(P_) / prod(Z_) : null;
    const show = (F) => (F.length ? F.slice(0, 4).map(f => f.txt).join('') + (F.length > 4 ? ` · … (${F.length - 4} more)` : '') : '1');
    const tf = h('div', 'tf-box', el);
    h('div', 'tf-lab', tf, `G(s) = ${shortLab(r.labels.output)} / ${shortLab(r.labels.input)}  [${u.gain}]`);
    const fr = h('div', 'tf-frac', tf);
    h('span', 'tf-k', fr, K === null ? 'K ·' : fmt(K, 3) + ' ·');
    const q = h('span', 'tf-q', fr); h('span', 'tf-n', q, show(Z_)); h('span', 'tf-d', q, show(P_));
    h('div', 'tf-lab', tf, `${m.n_zeros} zeros / ${m.n_poles} poles · s in rad/s · factors in order of frequency`);
    // what it means: a one-line verdict and tiles
    const light = half(r.poles).filter(p => p.zeta !== null && p.zeta >= 0 && p.zeta < 0.05 && Math.abs(p.im) > 1e-9).sort((a, b) => b.rel - a.rel).slice(0, 3);
    const zmin = m.rhp_zeros.length ? m.rhp_zeros.map(z => Math.hypot(z.re, z.im)).sort((a, b) => a - b)[0] : null;
    verdict(el, m.n_rhp_poles ? 'bad' : zmin ? 'warn' : 'good',
      m.n_rhp_poles ? 'Unstable open loop' : zmin ? 'Non-minimum phase' : 'Stable, minimum phase',
      m.n_rhp_poles ? `${m.n_rhp_poles} RHP pole(s): feedback must stabilise this channel` : zmin ? `RHP zero limits the feedback bandwidth to ≈ ${fw(zmin / 2)}` : 'no RHP poles or zeros');
    tiles(el, [
      m.integrator ? { k: 'Steady state', v: 'integrating', sub: 'a constant input ramps the output' }
        : { k: 'Steady-state gain', v: fmt(m.dc_gain, 3), unit: u.gain, sub: m.dc_gain < 0 ? 'opposite sign' : 'same sign' },
      { k: '−3 dB bandwidth', v: m.bandwidth_w ? fmt(wconv(m.bandwidth_w), 3) : '–', unit: m.bandwidth_w ? wunit() : '' },
      m.dominant && { k: 'Dominant mode', v: fmt(m.dominant.fn, 3), unit: 'Hz', sub: `${m.dominant.label} · ζ ${fmt(m.dominant.zeta * 100, 3)} %`, wide: true },
      { k: 'Lightly damped', v: light.length, unit: 'ζ < 5 %', sub: light.map(p => `${p.label} ${fmt(p.fn, 3)} Hz`).join(' · '), level: light.length ? 'warn' : 'good', wide: !!light.length },
      zmin !== null && { k: 'Bandwidth limit', v: fmt(wconv(zmin / 2), 3), unit: wunit(), sub: `½ × lowest RHP zero ${fw(zmin)}`, level: 'warn' },
      { k: 'Modes not in this pair', v: `${m.n_cancelled}/${m.full_order}`, sub: 'not excited or not seen' },
    ]);
    // poles and zeros, lowest frequency first
    const tbl = (title, rows, cols) => {
      const d = h('details', 'pz-tbl', el); h('summary', null, d, `${title} (${rows.length})`);
      const t = h('table', 'tbl', h('div', 'tbl-wrap', d));
      t.innerHTML = '<thead><tr>' + cols.map(c => `<th${c[2] ? ' class="l"' : ''}>${c[0]}</th>`).join('') + '</tr></thead>';
      const b = h('tbody', null, t);
      rows.forEach(rw => { const tr = h('tr', rw._bad ? 'bad' : null, b); cols.forEach(c => h('td', c[2] ? 'l' : 'num', tr, c[1](rw))); });
    };
    const fz = (z) => Math.hypot(z.re, z.im) / TWO_PI, zz = (z) => { const w = Math.hypot(z.re, z.im); return w > 1e-12 ? -z.re / w : null; };
    tbl('Poles', half(r.poles).sort((a, b) => a.fn - b.fn), [['f [Hz]', p => fmt(p.fn, 3)], ['ζ [%]', p => (p.zeta === null ? '–' : fmt(p.zeta * 100, 3))], ['Mode', p => p.label, 1]]);
    tbl('Zeros', half(r.zeros).map(z => Object.assign({ _bad: z.re > 1e-9 }, z)).sort((a, b) => fz(a) - fz(b)),
      [['f [Hz]', z => fmt(fz(z), 3)], ['ζ [%]', z => (zz(z) === null ? '–' : fmt(zz(z) * 100, 3))], ['', z => (z.re > 1e-9 ? 'RHP' : ''), 1]]);
  }

  const FAMCOL = () => ({ platform: S(1), tower: S(2), drivetrain: S(3), 'blade flap': S(7), 'blade edge': S(5), 'blade torsion': S(4), nacelle: S(6), rotor: S(8), controller: S(4), 'hydro/aero states': cssv('--muted'), other: S(8) });
  const SYMDASH = { collective: 'solid', BW: 'dash', FW: 'dot', cyclic: 'dashdot', '': 'solid' };
  const SYMMARK = { collective: 'circle', BW: 'tri-down', FW: 'tri-up', cyclic: 'diamond', '': 'circle' };
  const SYMNAME = { collective: 'collective (symmetric)', BW: 'backward whirl', FW: 'forward whirl', cyclic: 'cyclic (asymmetric)', '': '' };

  const PANELS = {
    // ---------------------------------------------------------------- operating points
    'ol:schedule': async (P) => {
      const s = st.summary, ops = s.ops, W = ops.map(o => o.wind);
      const sel = st.op;
      const mk = (title, sub, ys, unit, opts) => {
        const c = card(P, title, sub);
        const series = ys.map(([name, arr, col]) => ({ name, x: W, y: arr, color: col, marker: 'circle', size: 3.2, unit }));
        chartIn(c, Object.assign({ series, xlabel: 'Wind speed [m/s]', ylabel: unit ? `${title} [${unit}]` : title, xname: 'Wind', xunit: 'm/s', height: 230,
          annotations: [{ type: 'vline', x: W[sel], color: cssv('--accent'), dash: 'solid', width: 1 }] }, opts || {}));
        return c;
      };
      const g = h('div', 'row3', P);
      const c1 = mk.call(null, 'Rotor speed', '', [['Rotor speed', ops.map(o => o.rotor_rpm), S(1)]], 'rpm'); g.appendChild(c1.el);
      const c2 = mk('Blade pitch', '', [['Pitch', ops.map(o => o.pitch_deg), S(1)]], 'deg'); g.appendChild(c2.el);
      const c3 = mk('Generator power', '', [['Power', ops.map(o => o.power_kw === null ? null : o.power_kw / 1000), S(1)]], 'MW'); g.appendChild(c3.el);
      const g2 = h('div', 'row3', P);
      const c4 = mk('Generator torque', '', [['Torque', ops.map(o => o.gen_torque === null ? null : o.gen_torque / 1e6), S(1)]], 'MN·m'); g2.appendChild(c4.el);
      const c5 = mk('Tip-speed ratio', '', [['TSR', ops.map(o => o.tsr), S(1)]], ''); g2.appendChild(c5.el);
      const c6 = mk('Power and thrust coefficients', '', [['Cp', ops.map(o => o.cp), S(1)], ['Ct', ops.map(o => o.ct), S(2)]], ''); g2.appendChild(c6.el);

      // ---- consistency of the azimuth files: did every azimuth see the same operating point?
      const g3 = h('div', 'row2', P);
      // deviation of every file from its operating point's mean rotor speed; the files of one wind speed
      // are spread sideways in azimuth order (0°…360° → left…right) and joined, so drift shows as a slope,
      // 1P/3P ripple as a wave and a clean set as a flat line at 0
      const rc = card(g3, 'Rotor speed across the azimuth files', 'deviation of each file from the mean at its wind speed · files in azimuth order');
      const bad = ops.filter(o => (o.omega_check || {}).level === 'warning');
      metric(rc.metrics, 'Largest spread', fmt(Math.max(...ops.map(o => (o.omega_check || {}).spread || 0)) * 100, 3) + ' %', 'max − min');
      metric(rc.metrics, 'Points with a warning', bad.length ? bad.map(o => fmt(o.wind, 3)).join(', ') + ' m/s' : 'none', '', bad.length ? 'bad' : 'good');
      const lvlCol = (o) => ({ warning: cssv('--crit'), notice: cssv('--warn') }[(o.omega_check || {}).level] || S(1));
      const gaps = W.slice(1).map((w, i) => w - W[i]).filter(g => g > 0);
      const half = 0.3 * (gaps.length ? Math.min(...gaps) : 1);
      const rser = ops.map(o => {
        const fd = (o.file_diag || []).slice().sort((a, b) => (a.azimuth_deg ?? 0) - (b.azimuth_deg ?? 0));
        const mean = fd.reduce((s, f) => s + f.rotor_rpm, 0) / Math.max(1, fd.length);
        const oc = o.omega_check || {};
        return { name: `${fmt(o.wind, 3)} m/s`, x: fd.map(f => o.wind + half * (2 * (f.azimuth_deg ?? 180) / 360 - 1)), y: fd.map(f => (f.rotor_rpm / mean - 1) * 100),
          color: lvlCol(o), width: 1.4, marker: 'circle', size: 3.2, sortedX: false, nolegend: true,
          tips: fd.map(f => `<b>${fmt(o.wind, 3)} m/s</b> · ${f.file}<br>azimuth ${fmt(f.azimuth_deg, 4)}° · t ${fmt(f.time, 5)} s<br>Ω ${fmt(f.rotor_rpm, 6)} rpm · ${f.rotor_rpm >= mean ? '+' : ''}${fmt((f.rotor_rpm / mean - 1) * 100, 3)} %` +
            (oc.kind && oc.kind !== 'none' ? `<br>spread ${fmt(oc.spread * 100, 3)} % · ${oc.kind}` : '')) };
      });
      chartIn(rc, { series: rser, xlabel: 'Wind speed [m/s] (files spread by azimuth)', ylabel: 'ΔΩ from mean [%]', xname: 'Wind', height: 280, hover: 'nearest', legend: false,
        xpad: 0.04, yinclude: [-0.35, 0.35],
        annotations: [{ type: 'band', x0: W[sel] - half * 1.15, x1: W[sel] + half * 1.15, color: cssv('--accent'), opacity: 0.08 },
          { type: 'hline', y: 0.25, color: cssv('--muted'), label: '±0.25 % (warning: 0.5 % peak to peak)' }, { type: 'hline', y: -0.25, color: cssv('--muted') }] });
      renderAzimuthDebug(g3, ops[sel]);

      const tc = card(P, 'Operating-point table', 'values averaged over azimuth · click a row to inspect its azimuth files', { nochart: true });
      const wrap = h('div', 'tbl-wrap', tc.el);
      const t = h('table', 'tbl', wrap);
      t.innerHTML = '<thead><tr><th>Wind [m/s]</th><th>Rotor [rpm]</th><th>Ω spread</th><th>Pitch [deg]</th><th>Torque [MN·m]</th><th>Power [MW]</th><th>TSR</th><th>Cp</th><th>Ct</th><th>Platform pitch [deg]</th><th>Azimuths</th><th>Periodicity raw / MBC</th><th>Azimuth models: Δf / Δζ</th><th class="l">Notes</th></tr></thead>';
      const tb = h('tbody', null, t);
      ops.forEach((o, i) => {
        const tr = h('tr', 'click' + (i === sel ? ' sel' : ''), tb);
        const oc = o.omega_check || {}, az = o.az_sens;
        h('td', 'num', tr, fmt(o.wind, 4)); h('td', 'num', tr, fmt(o.rotor_rpm, 4));
        const sp = h('td', 'num', tr, oc.spread > 1e-5 ? fmt(oc.spread * 100, 2) + ' % ' + (oc.kind || '') : '0');
        if (oc.level === 'warning') sp.style.color = cssv('--crit-ink');
        [o.pitch_deg, o.gen_torque / 1e6, o.power_kw / 1000, o.tsr, o.cp, o.ct, o.ptfm_pitch_deg].forEach(v => h('td', 'num', tr, fmt(v, 4)));
        h('td', 'num', tr, o.n_az);
        h('td', 'num', tr, `${fmt(o.periodicity_raw * 100, 2)}% / ${fmt(o.periodicity_mbc * 100, 2)}%`);
        h('td', 'num', tr, az ? `${fmt(az.df * 100, 2)} % / ${fmt(az.dz * 100, 2)} pt` : '–').title = az ? `largest frequency change: ${az.df_mode}; largest damping change: ${az.dz_mode}` : '';
        h('td', 'l', tr, (o.warnings || []).join('; '));
        tr.onclick = () => { st.op = i; renderChips(); renderPanel(); };
      });
    },

    // ---------------------------------------------------------------- Campbell
    'ol:campbell': async (P) => {
      const s = st.summary, cs = st.campbell;
      const ctr = h('div', 'ctrls', P);
      const f1 = h('label', 'fl', ctr); h('span', 'lab', f1, 'Frequency');
      const seg = h('div', 'seg small', f1);
      [['nat', 'natural'], ['damp', 'damped']].forEach(([v, lab]) => { const b = h('button', ((cs.damped ? 'damp' : 'nat') === v) ? 'on' : '', seg, lab); b.onclick = () => { cs.damped = v === 'damp'; renderPanel(); }; });
      const f2 = h('label', 'fl', ctr); h('span', 'lab', f2, 'Max frequency [Hz]');
      const fx = h('input', null, f2); fx.type = 'number'; fx.step = '0.1'; fx.min = '0'; fx.value = cs.fmax || ''; fx.placeholder = 'auto';
      fx.onchange = () => { cs.fmax = fx.value ? +fx.value : null; renderPanel(); };
      const f3 = h('label', 'chk', ctr); const cb = h('input', null, f3); cb.type = 'checkbox'; cb.checked = cs.all; f3.appendChild(document.createTextNode(' include hydrodynamic / aerodynamic state modes'));
      cb.onchange = () => { cs.all = cb.checked; renderPanel(); };
      // open loop (plant alone) or closed loop (plant + ROSCO controller at every wind speed)
      const clReady = s.has_controller && s.closed && s.closed.length && !s.closed_pending;
      if (!clReady) cs.closed = false;
      const f0 = h('label', 'fl', ctr); h('span', 'lab', f0, 'Loop');
      const sg0 = h('div', 'seg small', f0);
      [[false, 'open'], [true, 'closed']].forEach(([v, lab]) => {
        const b = h('button', (!!cs.closed === v) ? 'on' : '', sg0, lab);
        if (v && !clReady) { b.disabled = true; b.title = s.closed_pending ? 'The closed loop is still being computed' : 'Needs a DISCON.IN'; b.style.opacity = 0.45; }
        b.onclick = () => { if (b.disabled) return; cs.closed = v; renderPanel(); };
      });
      ctr.insertBefore(f0, ctr.firstChild);
      f3.hidden = !!cs.closed;
      const data = await api('/api/campbell' + (cs.closed ? '?closed=1' : (cs.all ? '?all=1' : '')));
      const olData = cs.closed ? await api('/api/campbell') : null;
      if (!data.tracks.length) { h('div', 'empty', P, 'No oscillatory structural modes found.'); return; }
      const fcol = FAMCOL();
      const key = cs.damped ? 'fd' : 'fn';
      const fmaxData = Math.max(...data.tracks.map(t => Math.max(...t.points.map(p => p[key]))));
      const fmax = cs.fmax || Math.min(fmaxData * 1.08, 4);
      const byTrack = data.tracks.length <= 8;          // identity colour per mode when it fits the palette
      const famCount = {};
      const series = data.tracks.map((t, k) => {
        const n = famCount[t.family + t.sym] = (famCount[t.family + t.sym] || 0) + 1;
        const mk = t.sym ? SYMMARK[t.sym] : ['circle', 'square', 'diamond', 'tri-up', 'tri-down'][(n - 1) % 5];
        return { name: t.label, x: t.points.map(p => p.wind), y: t.points.map(p => p[key]),
          color: byTrack ? S(k + 1) : (fcol[t.family] || S(8)), dash: SYMDASH[t.sym || ''], marker: mk, size: 3.4, unit: 'Hz', _t: t };
      }).filter(sr => sr._t.points.some(p => p[key] <= fmax * 1.05));
      const winds = data.rotor.map(r => r.wind);
      const harm = [1, 3, 6, 9].map(n => ({ name: n + 'P', x: winds, y: data.rotor.map(r => r.f1p * n), color: cssv('--baseline'), width: 1.4, dash: 'long', nolegend: true, unit: 'Hz' }));
      // closed loop: the open-loop tracks stay as thin grey lines, so the shift caused by the controller shows
      const ghost = olData ? olData.tracks.filter(t => t.points.some(p => p[key] <= fmax * 1.05)) : [];
      const gser = (yk, sc) => ghost.map(t => ({ name: 'open loop', x: t.points.map(p => p.wind), y: t.points.map(p => p[yk] * sc), color: cssv('--muted'), width: 1, opacity: 0.7, nolegend: true, nohover: true, noextent: yk !== key }));
      const cc = card(P, cs.closed ? 'Campbell diagram, closed loop' : 'Campbell diagram',
        `${cs.damped ? 'damped' : 'natural'} frequency of each tracked mode · line/marker style = symmetry` + (cs.closed ? ' · grey: open loop · includes controller modes' : ''));
      // metrics: closest approach of any mode to 1P/3P/6P
      const closest = [];
      for (const n of [1, 3, 6]) {
        let best = null;
        for (const t of data.tracks) for (const p of t.points) {
          const f1 = data.rotor.find(r => r.wind === p.wind); if (!f1) continue;
          const d = Math.abs(p[key] - n * f1.f1p) / (n * f1.f1p);
          if (!best || d < best.d) best = { d, t, p };
        }
        if (best) closest.push([n, best]);
      }
      metric(cc.metrics, 'Tracked modes', data.tracks.length);
      metric(cc.metrics, 'Lowest mode', fmt(Math.min(...data.tracks.map(t => t.mean_f)), 3), 'Hz');
      closest.forEach(([n, b]) => { const m = metric(cc.metrics, `Closest to ${n}P`, `${fmt(b.d * 100, 2)}%`, `${b.t.label} @ ${fmt(b.p.wind, 3)} m/s`); if (b.d < 0.05) m.classList.add('bad'); });
      // legend for symmetry encoding
      const leg = h('div', 'metrics', cc.extra);
      Object.entries(SYMNAME).filter(([k]) => k && series.some(s2 => s2._t.sym === k)).forEach(([k, v]) => {
        const sp = h('span', 'badge', leg);
        const sv = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); sv.setAttribute('width', 28); sv.setAttribute('height', 12);
        sv.innerHTML = `<line x1="1" x2="27" y1="6" y2="6" stroke="${cssv('--ink2')}" stroke-width="2" ${LB.DASH[SYMDASH[k]] ? `stroke-dasharray="${LB.DASH[SYMDASH[k]]}"` : ''}/><path d="${LB.markerPath(SYMMARK[k], 14, 6, 3.5)}" fill="${cssv('--ink2')}"/>`;
        sp.appendChild(sv); sp.appendChild(document.createTextNode(v));
      });
      const ann = [];
      const lastW = winds[winds.length - 1];
      [1, 3, 6, 9].forEach(n => { const y = data.rotor[data.rotor.length - 1].f1p * n; if (y < fmax) ann.push({ type: 'point', x: lastW, y, label: n + 'P', size: 0.01, textColor: cssv('--muted'), anchor: 'end', dy: -5 }); });
      ann.push({ type: 'vline', x: s.ops[st.op].wind, color: cssv('--accent'), dash: 'solid', width: 1 });
      const ch1 = chartIn(cc, { series: harm.concat(gser(key, 1), series), xlabel: 'Wind speed [m/s]', ylabel: `Frequency [Hz]`, xname: 'Wind', xunit: 'm/s', height: 440,
        ydomain: [0, fmax], annotations: ann,
        yfmt: (v, sr, i) => fmt(v, 4) + (sr._t ? `  ζ ${fmt(sr._t.points[i].zeta * 100, 3)}%` : '') });
      const dc = card(P, 'Damping of the tracked modes', 'damping ratio vs wind speed');
      const dser = series.map(s2 => Object.assign({}, s2, { y: s2._t.points.map(p => p.zeta * 100), unit: '%' }));
      const ch2 = chartIn(dc, { series: gser('zeta', 100).concat(dser), xlabel: 'Wind speed [m/s]', ylabel: 'Damping ratio [%]', xname: 'Wind', xunit: 'm/s', height: 300, legend: false,
        annotations: [{ type: 'hline', y: 0, color: cssv('--crit'), dash: 'solid', width: 1 }] });
      LB.linkX([ch1, ch2]);
      ch1.opts.onToggle = (hid) => { ch2.hidden = new Set(hid); ch2.render(); };
      // table
      const tc = card(P, 'Tracked modes', '', { nochart: true });
      const wrap = h('div', 'tbl-wrap', tc.el);
      const t = h('table', 'tbl', wrap);
      t.innerHTML = '<thead><tr><th class="l">Mode</th><th class="l">Component</th><th class="l">Symmetry</th><th>f min [Hz]</th><th>f max [Hz]</th><th>ζ min [%]</th><th>at [m/s]</th><th>Points</th></tr></thead>';
      const tb = h('tbody', null, t);
      data.tracks.forEach(tr0 => {
        const tr = h('tr', null, tb);
        h('td', 'l', tr, tr0.label); h('td', 'l', tr, tr0.family); h('td', 'l', tr, SYMNAME[tr0.sym || ''] || '–');
        const fs = tr0.points.map(p => p[key]), zs = tr0.points.map(p => p.zeta);
        const iz = zs.indexOf(Math.min(...zs));
        h('td', 'num', tr, fmt(Math.min(...fs), 4)); h('td', 'num', tr, fmt(Math.max(...fs), 4));
        const tdz = h('td', 'num', tr, fmt(zs[iz] * 100, 3)); if (zs[iz] < 0) tdz.style.color = cssv('--crit-ink');
        h('td', 'num', tr, fmt(tr0.points[iz].wind, 3)); h('td', 'num', tr, tr0.points.length);
      });
    },

    // ---------------------------------------------------------------- modes at an operating point
    'ol:modes': async (P) => {
      const r = await api('/api/modes?op=' + st.op);
      const ctr = h('div', 'ctrls', P);
      const f3 = h('label', 'chk', ctr); const cb = h('input', null, f3); cb.type = 'checkbox'; cb.checked = !!st.showHydro; f3.appendChild(document.createTextNode(' show hydrodynamic / aerodynamic state modes'));
      cb.onchange = () => { st.showHydro = cb.checked; renderPanel(); };
      const f2 = h('label', 'fl', ctr); h('span', 'lab', f2, 'Max frequency [Hz]');
      const fx = h('input', null, f2); fx.type = 'number'; fx.step = '0.1'; fx.value = st.modeFmax || 2; fx.onchange = () => { st.modeFmax = +fx.value; renderPanel(); };
      const fmax = st.modeFmax || 2;
      const modes = r.modes.filter(m => (st.showHydro || m.family !== 'hydro/aero states') && m.fn <= fmax);
      const fcol = FAMCOL();
      const row = h('div', 'row2', P);
      const pc = card(row, 'Eigenvalues', `s-plane at ${fmt(r.wind, 3)} m/s · upper half-plane`);
      const fams = [...new Set(modes.map(m => m.family))];
      const series = fams.map(f => {
        const ms = modes.filter(m => m.family === f);
        return { name: f, type: 'scatter', x: ms.map(m => m.re), y: ms.map(m => m.im), color: fcol[f] || S(8), size: 4.5,
          markers: ms.map(m => SYMMARK[m.sym || '']), tips: ms.map(m => `<b>${m.label}</b><br>f<sub>n</sub> ${fmt(m.fn, 4)} Hz · ζ ${fmt(m.zeta * 100, 3)}%<br>λ = ${fmt(m.re, 4)} ${m.im >= 0 ? '+' : '−'} ${fmt(Math.abs(m.im), 4)}j`) };
      });
      const osc = modes.filter(m => m.oscillatory);
      metric(pc.metrics, 'Modes shown', modes.length);
      if (osc.length) { const lz = osc.reduce((a, b) => (b.zeta < a.zeta ? b : a)); const mm = metric(pc.metrics, 'Least damped', `${fmt(lz.zeta * 100, 3)}%`, lz.label); if (lz.zeta < 0) mm.classList.add('bad'); }
      const unst = r.modes.filter(m => m.re > 1e-9).length;
      metric(pc.metrics, 'Unstable', unst, '', unst ? 'bad' : 'good');
      const maxim = Math.max(0.1, ...modes.map(m => m.im));
      const zl = [0.05, 0.2, 0.5].map(z => ({ name: `ζ ${z}`, x: [0, -z * maxim / Math.sqrt(1 - z * z)], y: [0, maxim], color: cssv('--grid'), width: 1, dash: 'dash', nolegend: true, nohover: true, noextent: true }));
      chartIn(pc, { series: zl.concat(series), xlabel: 'Real part [1/s]', ylabel: 'Imaginary part [rad/s]', height: 380, hover: 'nearest', xzeroline: true, yinclude: 0, xinclude: 0 });
      const tc = card(row, 'Mode list', 'click a row for its participation', { nochart: true });
      const bars = h('div', null, tc.el);
      const wrap = h('div', 'tbl-wrap', tc.el);
      const t = h('table', 'tbl', wrap);
      t.innerHTML = '<thead><tr><th>f<sub>n</sub> [Hz]</th><th>f<sub>d</sub> [Hz]</th><th>ζ [%]</th><th class="l">Mode</th><th class="l">Symmetry</th><th>Share</th></tr></thead>';
      const tb = h('tbody', null, t);
      const showBars = (m) => {
        bars.innerHTML = '';
        h('div', 'lab', bars, m.label);
        const box = h('div', null, bars); box.style.margin = '6px 0 12px';
        m.top.forEach(([name, v]) => {
          const rr = h('div', null, box); rr.style.display = 'flex'; rr.style.alignItems = 'center'; rr.style.gap = '8px'; rr.style.fontSize = '12px'; rr.style.margin = '2px 0';
          const lab = h('span', null, rr, name); lab.style.width = '210px'; lab.style.color = cssv('--ink2');
          const b = h('span', null, rr); b.style.height = '10px'; b.style.borderRadius = '0 4px 4px 0'; b.style.background = S(1); b.style.width = Math.max(1, v * 260) + 'px';
          h('span', 'tnum', rr, fmt(v * 100, 3) + '%');
        });
      };
      modes.forEach((m, i) => {
        const tr = h('tr', 'click', tb);
        h('td', 'num', tr, fmt(m.fn, 4)); h('td', 'num', tr, fmt(m.fd, 4));
        const tz = h('td', 'num', tr, fmt(m.zeta * 100, 3)); if (m.zeta < 0) tz.style.color = cssv('--crit-ink');
        h('td', 'l', tr, m.label); h('td', 'l', tr, SYMNAME[m.sym || ''] || '–'); h('td', 'num', tr, fmt(m.share * 100, 3) + '%');
        tr.onclick = () => { $$('tr', tb).forEach(x => x.classList.remove('sel')); tr.classList.add('sel'); showBars(m); };
        if (i === 0) { tr.classList.add('sel'); showBars(m); }
      });
    },

    // ---------------------------------------------------------------- transfer function
    'ol:tf': async (P) => {
      const s = st.summary, ch = s.channels;
      const ctr = h('div', 'ctrls', P);
      channelSelect(ctr, 'Input', ch.inputs, st.io.input, v => { st.io.input = v; renderPanel(); }, inputOk);
      channelSelect(ctr, 'Output', ch.outputs, st.io.output, v => { st.io.output = v; renderPanel(); }, outputOk);
      presetButtons(ctr, (i, o) => { st.io = { input: i, output: o }; renderPanel(); });
      const wait = loading(P);
      const r = await api('/api/tf', { op: st.op, input: st.io.input, output: st.io.output });
      wait.remove();
      const m = r.metrics, u = r.units;
      P._insights = [(el) => tfInsights(el, r)];
      const bc = card(P, 'Bode diagram', `${r.labels.input} → ${r.labels.output} · ${fmt(r.wind, 3)} m/s · open loop`);
      metric(bc.metrics, 'DC gain', m.integrator ? '∞ (integrator)' : fmt(m.dc_gain, 4), m.integrator ? '' : u.gain);
      metric(bc.metrics, 'Peak gain', fmt(m.peak_gain, 4), `${u.gain} @ ${fw(m.peak_w)}`);
      metric(bc.metrics, 'Bandwidth (−3 dB)', m.bandwidth_w ? fw(m.bandwidth_w) : '–');
      metric(bc.metrics, 'RHP zeros', m.n_rhp_zeros, m.n_rhp_zeros ? 'non-minimum phase' : '', m.n_rhp_zeros ? 'bad' : 'good');
      metric(bc.metrics, 'RHP poles', m.n_rhp_poles, '', m.n_rhp_poles ? 'bad' : 'good');
      metric(bc.metrics, 'Effective order', `${m.n_poles} poles · ${m.n_zeros} zeros`);
      metric(bc.metrics, 'Cancelled pole-zero pairs', m.n_cancelled, `of ${m.full_order} states`);
      if (m.dominant) metric(bc.metrics, 'Dominant mode', m.dominant.label, `${fmt(m.dominant.fn, 3)} Hz · ζ ${fmt(m.dominant.zeta * 100, 3)}%`);
      const w = r.w.map(wconv);
      const hostPh = h('div', null, bc.el);
      bc.chart = new LB.Chart(bc.host, { title: 'Bode magnitude', series: [{ name: 'Magnitude', x: w, y: r.mag.map(db), fx: fxFreq(r.modal, 'db'), color: S(1), unit: 'dB' }], xlog: true, xlabel: '', ylabel: `Magnitude [dB ${u.gain}]`, xname: 'ω', xunit: wunit(), height: 260 });
      const phc = new LB.Chart(hostPh, { title: 'Bode phase', series: [{ name: 'Phase', x: w, y: r.phase, fx: fxFreq(r.modal, 'phase'), color: S(1), unit: 'deg' }], xlog: true, xlabel: wlabel(), ylabel: 'Phase [deg]', xname: 'ω', xunit: wunit(), height: 220 });
      LB.linkX([bc.chart, phc]);
      const pz = card(P, 'Pole-zero map', 'poles ×, zeros ○ · faded: pole cancelled by a zero (mode not excited or not observed)');
      const ctr2 = h('label', 'chk', pz.extra); const cbx = h('input', null, ctr2); cbx.type = 'checkbox'; cbx.checked = !!st.showCancelled; ctr2.appendChild(document.createTextNode(' show cancelled poles'));
      cbx.onchange = () => { st.showCancelled = cbx.checked; drawPZ(); };
      metric(pz.metrics, 'Poles', m.n_poles); metric(pz.metrics, 'Zeros', m.n_zeros);
      if (m.rhp_zeros.length) metric(pz.metrics, 'Lowest RHP zero', `${fmt(m.rhp_zeros[0].re, 3)} ± ${fmt(Math.abs(m.rhp_zeros[0].im), 3)}j`, '1/s', 'bad');
      metric(pz.metrics, 'Relative degree', m.rel_degree === null ? '–' : m.rel_degree);
      const drawPZ = () => {
        const series = [];
        const pl = r.poles.filter(p => p.im >= -1e-9 || true);
        series.push({ name: 'Poles', type: 'scatter', x: pl.map(p => p.re), y: pl.map(p => p.im), color: S(2), marker: 'x', size: 4.5,
          tips: pl.map(p => `<b>Pole</b> · ${p.label}<br>${fmt(p.re, 4)} ${p.im >= 0 ? '+' : '−'} ${fmt(Math.abs(p.im), 4)}j<br>f<sub>n</sub> ${fmt(p.fn, 4)} Hz · ζ ${p.zeta === null ? '–' : fmt(p.zeta * 100, 3) + '%'}<br>weight ${fmt(p.rel, 3)}`) });
        series.push({ name: 'Zeros', type: 'scatter', x: r.zeros.map(z => z.re), y: r.zeros.map(z => z.im), color: S(1), marker: 'circle', hollow: true, size: 4.5,
          tips: r.zeros.map(z => `<b>Zero</b><br>${fmt(z.re, 4)} ${z.im >= 0 ? '+' : '−'} ${fmt(Math.abs(z.im), 4)}j<br>|z| ${fmt(Math.hypot(z.re, z.im) / TWO_PI, 4)} Hz`) });
        if (st.showCancelled) series.push({ name: 'Cancelled poles', type: 'scatter', x: r.poles_cancelled.map(p => p.re), y: r.poles_cancelled.map(p => p.im), color: cssv('--muted'), marker: 'x', size: 3.5, opacity: 0.5,
          tips: r.poles_cancelled.map(p => `<b>Cancelled pole</b> · ${p.label}<br>${fmt(p.re, 4)} ${p.im >= 0 ? '+' : '−'} ${fmt(Math.abs(p.im), 4)}j`) });
        const lim = Math.max(1.2, ...r.poles.map(p => Math.abs(p.im)).filter(v => v < 30)) ;
        const opts = { series, xlabel: 'Real part [1/s]', ylabel: 'Imaginary part [rad/s]', height: 420, hover: 'nearest', xzeroline: true, equal: false,
          ydomain: [-Math.min(lim * 1.1, 6), Math.min(lim * 1.1, 6)] };
        if (pz.chart) pz.chart.set(opts); else chartIn(pz, opts);
      };
      drawPZ();
    },

    // ---------------------------------------------------------------- time response
    'ol:time': async (P) => {
      const s = st.summary, ch = s.channels, T = st.tio;
      const ctr = h('div', 'ctrls', P);
      channelSelect(ctr, 'Input', ch.inputs, T.input, v => { T.input = v; T.amp = null; renderPanel(); }, inputOk);
      channelSelect(ctr, 'Output', ch.outputs, T.output, v => { T.output = v; renderPanel(); }, outputOk);
      const inp = ch.inputs.find(c => c.i === T.input);
      const f1 = h('label', 'fl', ctr); h('span', 'lab', f1, 'Signal');
      const seg = h('div', 'seg small', f1);
      ['step', 'impulse'].forEach(k => { const b = h('button', T.kind === k ? 'on' : '', seg, k); b.onclick = () => { T.kind = k; $$('button', seg).forEach(x => x.classList.toggle('on', x === b)); }; });
      const f2 = h('label', 'fl', ctr); h('span', 'lab', f2, `Amplitude [${inp ? inp.disp_unit : ''}]`);
      const amp = h('input', null, f2); amp.type = 'number'; amp.step = 'any';
      if (T.amp === null) T.amp = defaultAmp(inp);
      amp.value = T.amp;
      const f3 = h('label', 'fl', ctr); h('span', 'lab', f3, 'Duration [s]');
      const dur = h('input', null, f3); dur.type = 'number'; dur.placeholder = 'auto'; dur.value = T.T;
      const go = h('button', 'btn primary', ctr, 'Generate');
      const c = card(P, 'Time response', '');
      const run = async () => {
        T.amp = +amp.value; T.T = dur.value;
        c.metrics.innerHTML = ''; c.host.innerHTML = '';
        const wait = loading(c.host);
        const r = await api('/api/time', { op: st.op, input: T.input, output: T.output, kind: T.kind, amp: T.amp, T: T.T || null });
        wait.remove();
        c.el.querySelector('.sub').textContent = `${T.kind} of ${fmt(T.amp, 4)} ${r.units.input} on ${r.labels.input} → ${r.labels.output} · ${fmt(r.wind, 3)} m/s · open loop`;
        const m = r.metrics;
        metric(c.metrics, 'Peak', fmt(m.peak, 4), `${r.units.output} @ ${fmt(m.t_peak, 3)} s`);
        metric(c.metrics, 'Final value', m.final === null ? 'not settled' : fmt(m.final, 4), m.final === null ? '' : r.units.output);
        if (T.kind === 'step') {
          metric(c.metrics, 'Overshoot', m.overshoot_pct === null ? '–' : fmt(m.overshoot_pct, 3) + '%');
          metric(c.metrics, 'Rise time 10–90%', m.t_rise === null ? '–' : fmt(m.t_rise, 3) + ' s');
          metric(c.metrics, 'Settling 2%', m.t_settle === null ? '–' : fmt(m.t_settle, 3) + ' s');
          if (m.undershoot_pct) metric(c.metrics, 'Initial undershoot', fmt(m.undershoot_pct, 3) + '%', '', 'bad');
        }
        chartIn(c, { series: [{ name: r.labels.output, x: r.t, y: r.y, fx: fxTime(r.modal, T.kind, T.amp), color: S(1), unit: r.units.output }], xlabel: 'Time [s]', ylabel: `${shortLab(r.labels.output)} [${r.units.output}]`, xname: 't', xunit: 's', height: 360 });
      };
      go.onclick = () => run().catch(e => errorBox(c.host, e));
      await run();
    },

    // ---------------------------------------------------------------- sweep across wind speeds
    'ol:sweep': async (P) => {
      const s = st.summary, ch = s.channels;
      const ctr = h('div', 'ctrls', P);
      channelSelect(ctr, 'Input', ch.inputs, st.io.input, v => { st.io.input = v; renderPanel(); }, inputOk);
      channelSelect(ctr, 'Output', ch.outputs, st.io.output, v => { st.io.output = v; renderPanel(); }, outputOk);
      presetButtons(ctr, (i, o) => { st.io = { input: i, output: o }; renderPanel(); });
      const wait = loading(P, 'Computing every operating point…');
      const r = await api('/api/sweep', { input: st.io.input, output: st.io.output });
      wait.remove();
      const cols = seqRamp(r.rows.length);
      const w = r.w.map(wconv);
      const bc = card(P, 'Bode magnitude at every operating point', `${r.labels.input} → ${r.labels.output}`);
      const series = r.rows.map((row, i) => ({ name: `${fmt(row.wind, 3)} m/s`, x: w, y: row.mag.map(db), fx: fxFreq(row.modal, 'db'), color: cols[i], width: i === st.op ? 3.2 : 1.4, unit: 'dB' }));
      const rows = r.rows;
      metric(bc.metrics, 'Selected', `${fmt(rows[st.op].wind, 3)} m/s`, 'bold line');
      const dcs = rows.map(x => x.dc).filter(v => v !== null);
      if (dcs.length) metric(bc.metrics, 'DC gain range', `${fmt(Math.min(...dcs), 3)} … ${fmt(Math.max(...dcs), 3)}`, `${r.units.output}/${r.units.input}`);
      const nmp = rows.filter(x => x.rhp_zeros > 0).map(x => fmt(x.wind, 3));
      metric(bc.metrics, 'Non-minimum phase at', nmp.length ? nmp.join(', ') : 'none', nmp.length ? 'm/s' : '', nmp.length ? 'bad' : 'good');
      chartIn(bc, { series, xlog: true, xlabel: wlabel(), ylabel: `Magnitude [dB ${r.units.output}/${r.units.input}]`, xname: 'ω', xunit: wunit(), height: 400,
        colorbar: { label: 'Wind speed [m/s]', min: rows[0].wind, max: rows[rows.length - 1].wind, colors: cols } });
      const g = h('div', 'row2', P);
      const c2 = card(g, 'DC gain vs wind speed', `${r.units.output}/${r.units.input}`);
      chartIn(c2, { series: [{ name: 'DC gain', x: rows.map(x => x.wind), y: rows.map(x => x.dc), color: S(1), marker: 'circle', size: 3.2 }], xlabel: 'Wind speed [m/s]', ylabel: `DC gain [${r.units.output}/${r.units.input}]`, xname: 'Wind', xunit: 'm/s', height: 260,
        annotations: [{ type: 'vline', x: rows[st.op].wind, color: cssv('--accent'), dash: 'solid', width: 1 }] });
      const c3 = card(g, 'Peak gain and its frequency', '');
      chartIn(c3, { series: [{ name: 'Peak-gain frequency', x: rows.map(x => x.wind), y: rows.map(x => wconv(x.peak_w)), color: S(2), marker: 'circle', size: 3.2, unit: wunit() }],
        xlabel: 'Wind speed [m/s]', ylabel: `Frequency of peak gain [${wunit()}]`, xname: 'Wind', xunit: 'm/s', height: 260 });
    },

    // ---------------------------------------------------------------- controllability, observability, Hankel SVs
    'ol:struct': async (P) => {
      const s = st.summary, ch = s.channels, Q = st.sio;
      const ctr = h('div', 'ctrls', P);
      const f0 = h('label', 'fl', ctr); h('span', 'lab', f0, 'Channels');
      const seg = h('div', 'seg small', f0);
      [['set', 'control set'], ['pair', 'single pair']].forEach(([v, lab]) => { const b = h('button', Q.mode === v ? 'on' : '', seg, lab); b.onclick = () => { Q.mode = v; renderPanel(); }; });
      let ins, outs;
      if (Q.mode === 'pair') {
        channelSelect(ctr, 'Input', ch.inputs, st.io.input, v => { st.io.input = v; renderPanel(); }, inputOk);
        channelSelect(ctr, 'Output', ch.outputs, st.io.output, v => { st.io.output = v; renderPanel(); }, outputOk);
        ins = [st.io.input]; outs = [st.io.output];
      } else {
        const pick = (label, std, names, arr) => {
          const f = h('div', 'fl', ctr); h('span', 'lab', f, label);
          const box = h('div', 'presets', f);
          Object.entries(names).forEach(([k, lab]) => {
            const i = std[k]; if (i === null || i === undefined) return;
            const l = h('label', 'chk', box); const cb = h('input', null, l); cb.type = 'checkbox'; cb.checked = arr.includes(i);
            l.appendChild(document.createTextNode(' ' + lab)); l.title = (label === 'Inputs' ? ch.inputs : ch.outputs).find(c => c.i === i).label;
            cb.onchange = () => { const k2 = arr.indexOf(i); if (cb.checked && k2 < 0) arr.push(i); if (!cb.checked && k2 >= 0) arr.splice(k2, 1); renderPanel(); };
          });
        };
        pick('Inputs', s.std_inputs, { pitch: 'collective pitch', torque: 'generator torque', wind: 'wind speed', wave: 'wave elevation' }, Q.ins);
        pick('Outputs', s.std_outputs, { genspeed: 'generator speed', rotspeed: 'rotor speed', genpwr: 'power', ptfmpitch: 'platform pitch', nacacc_fa: 'nacelle FA accel', ttdspfa: 'tower-top FA disp' }, Q.outs);
        ins = Q.ins; outs = Q.outs;
      }
      const f2 = h('label', 'fl', ctr); h('span', 'lab', f2, 'Max frequency [Hz]');
      const fx = h('input', null, f2); fx.type = 'number'; fx.step = '0.1'; fx.value = st.modeFmax || 2; fx.onchange = () => { st.modeFmax = +fx.value; renderPanel(); };
      const f3 = h('label', 'chk', ctr); const hb = h('input', null, f3); hb.type = 'checkbox'; hb.checked = !!st.showHydro; f3.appendChild(document.createTextNode(' show hydrodynamic / aerodynamic state modes'));
      hb.onchange = () => { st.showHydro = hb.checked; renderPanel(); };
      if (!ins.length || !outs.length) { h('div', 'empty', P, 'Choose at least one input and one output.'); return; }
      const wait = loading(P);
      const sweepP = api('/api/structure_sweep', { inputs: ins, outputs: outs }).catch(e => ({ failed: e }));
      const r = await api('/api/structure', { op: st.op, inputs: ins, outputs: outs });
      wait.remove();
      const fmax = st.modeFmax || 2, fcol = FAMCOL(), tol = r.tol;
      const io = `${r.inputs.map(x => shortLab(x.label)).join(', ')} → ${r.outputs.map(x => shortLab(x.label)).join(', ')}`;

      // ---- Kalman decomposition
      const kc = card(P, 'Controllability and observability', `${io} · ${fmt(r.wind, 3)} m/s`, { nochart: true });
      metric(kc.metrics, 'States', r.n);
      metric(kc.metrics, 'Controllable', `${r.rank_c} / ${r.n}`, '', r.rank_c < r.n ? 'bad' : 'good');
      metric(kc.metrics, 'Observable', `${r.rank_o} / ${r.n}`, '', r.rank_o < r.n ? 'bad' : 'good');
      metric(kc.metrics, 'Minimal order', r.n_min, 'controllable and observable');
      metric(kc.metrics, 'Stabilisable', r.stabilizable ? 'yes' : 'no', '', r.stabilizable ? 'good' : 'bad');
      metric(kc.metrics, 'Detectable', r.detectable ? 'yes' : 'no', '', r.detectable ? 'good' : 'bad');
      const lists = h('div', 'notes', kc.el);
      const lst = (title, arr) => h('div', null, lists, arr.length ? `${title}: ` + arr.map(u => `${u.label || 'unlabelled'} (${fmt(u.fn, 3)} Hz, ζ ${fmt(u.zeta * 100, 3)}%)`).join(' · ') : `${title}: none`);
      lst('Uncontrollable modes', r.uncontrollable);
      lst('Unobservable modes', r.unobservable);
      h('div', null, lists, 'Ranks from the orthogonal staircase (Kalman decomposition) on a balanced model, not from the ill-conditioned matrix [B AB A²B …]. Channels in display units (deg, deg/s, …).');

      // ---- modes in the controllability–observability plane and in the s-plane
      const modes = r.modes.filter(m => (st.showHydro || m.family !== 'hydro/aero states') && m.fn <= fmax);
      const floor = 1e-16, lg = (v) => Math.max(v, floor);
      const tipOf = (m) => `<b>${m.label}</b><br>f<sub>n</sub> ${fmt(m.fn, 4)} Hz · ζ ${fmt(m.zeta * 100, 3)}%<br>controllability ${fmt(m.ctrb, 3)}${m.weak_c ? ' (uncontrollable)' : ''}<br>observability ${fmt(m.obsv, 3)}${m.weak_o ? ' (unobservable)' : ''}<br>modal Hankel ${m.hankel === null ? '∞' : fmt(m.hankel, 3)}`;
      const row = h('div', 'row2', P);
      const pc = card(row, 'Controllability–observability plane', 'one point per mode · PBH angle measures (1 = fully aligned, 0 = fails the PBH test)');
      const fams = [...new Set(modes.map(m => m.family))];
      const ser = fams.map(f => {
        const ms = modes.filter(m => m.family === f);
        return { name: f, type: 'scatter', x: ms.map(m => lg(m.ctrb)), y: ms.map(m => lg(m.obsv)), color: fcol[f] || S(8), size: 4.5,
          markers: ms.map(m => SYMMARK[m.sym || '']), hollowAt: ms.map(m => m.weak_c || m.weak_o), tips: ms.map(tipOf) };
      });
      const both = modes.filter(m => !m.weak_c && !m.weak_o);
      if (both.length) { const wc = both.reduce((a, b) => (b.ctrb < a.ctrb ? b : a)), wo = both.reduce((a, b) => (b.obsv < a.obsv ? b : a)); metric(pc.metrics, 'Least controllable', fmt(wc.ctrb, 3), wc.label); metric(pc.metrics, 'Least observable', fmt(wo.obsv, 3), wo.label); }
      metric(pc.metrics, 'Hollow', 'fails PBH test');
      chartIn(pc, { series: ser, xlog: true, ylog: true, xlabel: 'Modal controllability  |wᵢB| / (|wᵢ||B|)', ylabel: 'Modal observability', height: 400, hover: 'nearest',
        annotations: [{ type: 'vline', x: tol, color: cssv('--crit'), label: 'PBH tol' }, { type: 'hline', y: tol, color: cssv('--crit'), label: 'PBH tol' }] });
      const ec = card(row, 'Eigenvalues by modal Hankel value', `s-plane · colour = |C vᵢ||wᵢ B| / 2|Re λᵢ| · hollow = uncontrollable or unobservable`);
      const hv = modes.map(m => (m.hankel === null ? null : Math.log10(Math.max(m.hankel, floor))));
      const fin = hv.filter(v => v !== null), hlo = fin.length ? Math.max(Math.min(...fin), Math.max(...fin) - 8) : 0, hhi = fin.length ? Math.max(...fin) : 1;
      const ramp = seqRamp(11);
      const colOf = (v) => (v === null ? cssv('--crit') : ramp[Math.round(Math.max(0, Math.min(1, (v - hlo) / ((hhi - hlo) || 1))) * 10)]);
      const maxim = Math.max(0.1, ...modes.map(m => m.im));
      const zl = [0.05, 0.2, 0.5].map(z => ({ name: `ζ ${z}`, x: [0, -z * maxim / Math.sqrt(1 - z * z)], y: [0, maxim], color: cssv('--grid'), width: 1, dash: 'dash', nolegend: true, nohover: true, noextent: true }));
      chartIn(ec, { series: zl.concat([{ name: 'modes', type: 'scatter', x: modes.map(m => m.re), y: modes.map(m => m.im), color: ramp[10], colors: hv.map(colOf), size: 5,
        markers: modes.map(m => SYMMARK[m.sym || '']), hollowAt: modes.map(m => m.weak_c || m.weak_o), tips: modes.map(tipOf) }]),
        xlabel: 'Real part [1/s]', ylabel: 'Imaginary part [rad/s]', height: 400, hover: 'nearest', xzeroline: true, xinclude: 0, yinclude: 0, legend: false,
        colorbar: { label: 'log₁₀ modal Hankel value', min: hlo, max: hhi, colors: ramp } });

      // ---- Hankel singular values
      const hc = card(P, 'Hankel singular values', `stable part of ${io} · ${fmt(r.wind, 3)} m/s · balanced-truncation error bound 2·Σ σ beyond order r`);
      const hsv = r.hsv, s1 = hsv.length ? hsv[0] : 0, keep = (v) => (v > s1 * 1e-13 ? v : null);
      metric(hc.metrics, 'σ₁', fmt(s1, 4));
      metric(hc.metrics, 'Order for bound ≤ 1% of σ₁', r.order_1pct === null ? '–' : r.order_1pct, `of ${hsv.length} stable states`);
      metric(hc.metrics, 'Minimal order', r.n_min);
      metric(hc.metrics, 'Unstable / marginal modes', r.n_unstable, r.n_unstable ? 'excluded (σ = ∞)' : '', r.n_unstable ? 'bad' : '');
      const idx = hsv.map((_, i) => i + 1);
      chartIn(hc, { series: [
        { name: 'Hankel singular value σᵣ', x: idx, y: hsv.map(keep), color: S(1), marker: 'circle', size: 3.6 },
        { name: 'Error bound after truncating to order r', x: idx, y: r.bound.map(keep), color: S(2), dash: 'dash', marker: 'square', size: 2.6 }],
        ylog: true, xlabel: 'Order r', ylabel: 'Value (display units)', xname: 'r', height: 320, xpad: 0.02,
        annotations: r.order_1pct ? [{ type: 'vline', x: r.order_1pct, color: cssv('--accent'), label: '1% bound', dash: 'dash' }] : [] });

      // ---- per-mode table
      const tc = card(P, 'Modal controllability and observability', `f ≤ ${fmax} Hz · sorted by frequency`, { nochart: true });
      const wrap = h('div', 'tbl-wrap', tc.el);
      const t = h('table', 'tbl', wrap);
      t.innerHTML = '<thead><tr><th>f<sub>n</sub> [Hz]</th><th>ζ [%]</th><th class="l">Mode</th><th class="l">Symmetry</th><th>Controllability</th><th>Observability</th><th>Modal Hankel</th><th class="l">PBH test</th></tr></thead>';
      const tb = h('tbody', null, t);
      modes.forEach(m => {
        const tr = h('tr', null, tb);
        h('td', 'num', tr, fmt(m.fn, 4)); h('td', 'num', tr, fmt(m.zeta * 100, 3));
        h('td', 'l', tr, m.label); h('td', 'l', tr, SYMNAME[m.sym || ''] || '–');
        const a = h('td', 'num', tr, fmt(m.ctrb, 3)); if (m.weak_c) a.style.color = cssv('--crit-ink');
        const b = h('td', 'num', tr, fmt(m.obsv, 3)); if (m.weak_o) b.style.color = cssv('--crit-ink');
        h('td', 'num', tr, m.hankel === null ? '∞' : fmt(m.hankel, 3));
        h('td', 'l', tr, m.weak_c && m.weak_o ? 'uncontrollable and unobservable' : m.weak_c ? 'uncontrollable' : m.weak_o ? 'unobservable' : 'passes');
      });

      // ---- across wind speeds
      const sw = await sweepP;
      const g = h('div', 'row2', P);
      if (sw.failed) { errorBox(g, sw.failed); return; }
      const rows = sw.rows.filter(x => !x.error), W = rows.map(x => x.wind);
      const vl = { type: 'vline', x: s.ops[st.op].wind, color: cssv('--accent'), dash: 'solid', width: 1 };
      const nh = Math.min(6, Math.max(0, ...rows.map(x => x.hsv.length)));
      const hs = card(g, 'Hankel singular values across wind speeds', 'largest six');
      chartIn(hs, { series: Array.from({ length: nh }, (_, k) => ({ name: `σ${k + 1}`, x: W, y: rows.map(x => (x.hsv[k] > x.hsv[0] * 1e-13 ? x.hsv[k] : null)), color: S(k + 1), marker: 'circle', size: 3 })),
        ylog: true, xlabel: 'Wind speed [m/s]', ylabel: 'Hankel singular value', xname: 'Wind', xunit: 'm/s', height: 300, annotations: [vl] });
      const rc = card(g, 'Ranks across wind speeds', 'Kalman decomposition at every operating point');
      chartIn(rc, { series: [
        { name: 'States', x: W, y: rows.map(x => x.n), color: cssv('--muted'), dash: 'dash' },
        { name: 'Controllable', x: W, y: rows.map(x => x.rank_c), color: S(1), marker: 'circle', size: 3 },
        { name: 'Observable', x: W, y: rows.map(x => x.rank_o), color: S(2), marker: 'square', size: 3 },
        { name: 'Minimal order', x: W, y: rows.map(x => x.n_min), color: S(3), marker: 'diamond', size: 3 }],
        xlabel: 'Wind speed [m/s]', ylabel: 'Dimension', xname: 'Wind', xunit: 'm/s', height: 300, yinclude: 0, annotations: [vl] });
    },

    // ================================================================ CLOSED LOOP
    'cl:summary': async (P) => {
      const s = st.summary, rows = s.closed.filter(r => !r.error), W = rows.map(r => r.wind);
      const g = h('div', 'row2', P);
      const pitchRows = rows;
      const sel = s.ops[st.op].wind;
      const vl = { type: 'vline', x: sel, color: cssv('--accent'), dash: 'solid', width: 1 };
      // loop delay: the controller runs at a fixed time step; a pure delay is the standard linear stand-in
      if (st.delayMs === undefined) st.delayMs = s.dt ? +(1500 * s.dt).toPrecision(4) : '';
      const ctr = h('div', 'ctrls', P);
      const fd = h('label', 'fl', ctr); h('span', 'lab', fd, 'Loop delay [ms]');
      const di = h('input', null, fd); di.type = 'number'; di.min = '0'; di.step = 'any'; di.placeholder = 'off'; di.value = st.delayMs;
      di.onchange = () => { st.delayMs = di.value === '' ? '' : Math.max(0, +di.value); renderPanel(); };
      h('span', 'muted', ctr, s.dt ? `default 1.5 × DT (DT = ${fmt(s.dt * 1000, 3)} ms from the .fst): half a step for the hold, one step for the controller call`
        : 'time step unknown (no .fst given): enter 1.5 × your controller time step, e.g. 37.5 for DT = 25 ms; empty = no delay').style.alignSelf = 'center';
      const tau = st.delayMs === '' ? 0 : +st.delayMs / 1000;
      let dly = null;
      if (tau > 0) { try { dly = (await api('/api/delay?tau=' + tau)).rows; } catch (e) { errorBox(P, e); } }
      const dOf = (r) => (dly ? dly.find(x => x.op === r.op) : null) || {};
      const mk = (parent, title, sub, y, unit, ref, opts) => {
        const c = card(parent, title, sub);
        const series = [{ name: dly && opts && opts.delayed ? 'no delay' : title, x: W, y, color: S(1), marker: 'circle', size: 3.4, unit, markers: rows.map(r => r.stable ? 'circle' : 'x'), colors: rows.map(r => r.stable ? S(1) : cssv('--crit')) }];
        if (dly && opts && opts.delayed) series.push({ name: `with ${fmt(tau * 1000, 3)} ms delay`, x: W, y: rows.map(r => opts.delayed(dOf(r))), color: S(2), dash: 'dash', marker: 'square', size: 3, unit,
          markers: rows.map(r => (dOf(r).stable === false ? 'x' : 'square')) });
        chartIn(c, Object.assign({ series,
          xlabel: 'Wind speed [m/s]', ylabel: opts && opts.ylabel ? opts.ylabel : `${title} [${unit}]`, xname: 'Wind', xunit: 'm/s', height: 250,
          annotations: [vl].concat(ref ? [{ type: 'hline', y: ref[0], label: ref[1], color: cssv('--muted') }] : []) }, opts || {}));
        return c;
      };
      const unstable = rows.filter(r => !r.stable);
      P._insights = [(el) => summaryInsights(el, rows, dly, tau)];
      const hc = card(P, 'Closed-loop stability', `ROSCO controller from DISCON closed around the linearisation at each operating point`, { nochart: true });
      P.insertBefore(hc.el, g);
      metric(hc.metrics, 'Operating points', rows.length);
      metric(hc.metrics, 'Unstable', unstable.length, unstable.length ? unstable.map(r => fmt(r.wind, 3)).join(', ') + ' m/s' : '', unstable.length ? 'bad' : 'good');
      const gms = rows.filter(r => r.stable && r.gm_db !== null), pms = rows.filter(r => r.stable && r.pm !== null), sms = rows.filter(r => r.stable);
      if (gms.length) { const x = gms.reduce((a, b) => (b.gm_db < a.gm_db ? b : a)); metric(hc.metrics, 'Worst gain margin', fmt(x.gm_db, 3) + ' dB', `@ ${fmt(x.wind, 3)} m/s`, x.gm_db < 6 ? 'bad' : ''); }
      if (pms.length) { const x = pms.reduce((a, b) => (b.pm < a.pm ? b : a)); metric(hc.metrics, 'Worst phase margin', fmt(x.pm, 3) + '°', `@ ${fmt(x.wind, 3)} m/s`, x.pm < 30 ? 'bad' : ''); }
      if (sms.length) { const x = sms.reduce((a, b) => (b.s_m < a.s_m ? b : a)); metric(hc.metrics, 'Worst stability margin s_m', fmt(x.s_m, 3), `@ ${fmt(x.wind, 3)} m/s`, x.s_m < 0.4 ? 'bad' : ''); }
      const nq = rows.every(r => r.nyquist_ok);
      metric(hc.metrics, 'Nyquist Z = N + P', nq ? 'consistent at all points' : 'check', '', nq ? 'good' : 'bad');
      const dks = rows.filter(r => r.stable && r.disk_gm_db !== null && r.disk_gm_db !== undefined);
      if (dks.length) { const x = dks.reduce((a, b) => (b.disk_gm_db < a.disk_gm_db ? b : a)); metric(hc.metrics, 'Worst disk margin', `±${fmt(x.disk_gm_db, 3)} dB and ±${fmt(x.disk_pm, 3)}°`, `together · @ ${fmt(x.wind, 3)} m/s`, x.disk_gm_db < 3 ? 'bad' : ''); }
      if (dly) {
        const du = dly.filter(x => x.stable === false), dp = dly.filter(x => x.stable && x.pm !== null);
        metric(hc.metrics, `With ${fmt(tau * 1000, 3)} ms delay`, du.length ? `unstable at ${du.map(x => fmt(x.wind, 3)).join(', ')} m/s` : 'stable at all points',
          dp.length ? `worst PM ${fmt(Math.min(...dp.map(x => x.pm)), 3)}°` : '', du.length ? 'bad' : 'good');
      }
      const notes = [...new Set(rows.flatMap(r => r.notes || []))];
      if (notes.length) { const nb = h('div', 'notes', hc.el); notes.forEach(n => h('div', null, nb, n)); }
      P.insertBefore(ctr, hc.el);
      mk(g, 'Gain margin', 'minimum |GM|; ✕ = closed loop unstable', rows.map(r => r.gm_db), 'dB', [6, '6 dB'], { delayed: x => x.gm_db });
      mk(g, 'Phase margin', '', rows.map(r => r.pm), 'deg', [45, '45°'], { delayed: x => x.pm });
      const g2 = h('div', 'row2', P);
      mk(g2, 'Stability margin s_m', 'min |1 + L| = 1/‖S‖∞', rows.map(r => r.s_m), '–', [0.5, '0.5'], { yinclude: 0, delayed: x => x.s_m });
      mk(g2, 'Least-damped closed-loop mode', 'damping ratio · below 2 Hz', rows.map(r => r.min_zeta === null ? null : r.min_zeta * 100), '%', [0, ''], { ylabel: 'Damping ratio [%]', yfmt: (v, sr, i) => `${fmt(v, 3)} %  ${rows[i].min_zeta_label || ''}` });
      const tc = card(P, 'Per operating point', 'change the loop that is closed with the Region selector', { nochart: true });
      const wrap = h('div', 'tbl-wrap', tc.el);
      const t = h('table', 'tbl', wrap);
      t.innerHTML = '<thead><tr><th>Wind [m/s]</th><th class="l">Region</th><th>Kp</th><th>Ki</th><th>GM [dB]</th><th>ω<sub>GM</sub></th><th>PM [deg]</th><th>ω<sub>c</sub></th>' + (dly ? '<th>PM with delay [deg]</th>' : '') + '<th>Disk GM [dB] / PM [deg]</th><th>Delay margin [s]</th><th>s<sub>m</sub></th><th>‖S‖∞ [dB]</th><th>RHP poles (P→Z)</th><th class="l">Status</th><th class="l">Least-damped mode</th></tr></thead>';
      const tb = h('tbody', null, t);
      s.closed.forEach((r) => {
        const tr = h('tr', 'click' + (r.op === st.op ? ' sel' : ''), tb);
        h('td', 'num', tr, fmt(r.wind, 3));
        const tdr = h('td', 'l', tr);
        const selR = h('select', null, tdr);
        [['auto', 'Auto'], ['pitch', 'Pitch (R3)'], ['torque', 'Torque (R2)'], ['both', 'Both loops (R2½)']].forEach(([v, lab]) => { const o = h('option', null, selR, lab); o.value = v; });
        selR.value = st.regionSel && st.regionSel[r.op] || 'auto';
        selR.onclick = (e) => e.stopPropagation();
        selR.onchange = async () => {
          st.regionSel = st.regionSel || {}; st.regionSel[r.op] = selR.value;
          const nr = await api('/api/region', { op: r.op, region: selR.value });
          const k = s.closed.findIndex(c => c.op === r.op); s.closed[k] = nr; st.cache = {};
          renderChips(); renderPanel();
        };
        if (r.error) { const td = h('td', 'l', tr, r.error); td.colSpan = 14; return; }
        const reg = r.region === 'pitch';
        h('td', 'num', tr, fmt(reg ? r.Kp : r.VS_Kp, 4)); h('td', 'num', tr, fmt(reg ? r.Ki : r.VS_Ki, 4));
        h('td', 'num', tr, fmt(r.gm_db, 3)); h('td', 'num', tr, fw(r.w_gm));
        h('td', 'num', tr, fmt(r.pm, 3)); h('td', 'num', tr, fw(r.w_pm));
        if (dly) { const x = dOf(r); const td = h('td', 'num', tr, x.stable === false ? 'unstable' : fmt(x.pm, 3)); if (x.stable === false || (x.pm !== null && x.pm < 30)) td.style.color = cssv('--crit-ink'); }
        h('td', 'num', tr, r.disk_gm_db === null || r.disk_gm_db === undefined ? '–' : `±${fmt(r.disk_gm_db, 3)} / ±${fmt(r.disk_pm, 3)}`);
        h('td', 'num', tr, fmt(r.dm, 3)); h('td', 'num', tr, fmt(r.s_m, 3)); h('td', 'num', tr, fmt(r.Smax_db, 3));
        h('td', 'num', tr, `${r.P} → ${r.Z}`);
        const stt = h('td', 'l', tr); const b = h('span', 'badge ' + (r.stable ? 'good' : 'bad'), stt); const d = h('i', null, b); d.style.background = r.stable ? cssv('--good') : cssv('--crit'); b.appendChild(document.createTextNode(r.stable ? 'stable' : 'unstable'));
        h('td', 'l', tr, r.min_zeta_label ? `${r.min_zeta_label} (${fmt(r.min_zeta * 100, 3)}%)` : '–');
        tr.onclick = () => { st.op = r.op; renderChips(); renderPanel(); };
      });

      // ---- pitch and torque loops closed together (matters around rated, where both act)
      const ic = card(P, 'Pitch–torque interaction', 'both PI loops closed on generator speed at once, against each loop alone');
      const iw = loading(ic.host, 'Closing both loops at every operating point…');
      let it;
      try { it = await api('/api/interaction'); } catch (e) { iw.remove(); errorBox(ic.host, e); return; }
      iw.remove();
      const ir = it.rows.filter(x => !x.error);
      if (!ir.length) { h('div', 'empty', ic.host, (it.rows[0] || {}).error || 'Needs both a pitch and a generator-torque input in the linearisation.'); return; }
      const ub = ir.filter(x => !x.both_stable);
      metric(ic.metrics, 'Both loops closed', ub.length ? `unstable at ${ub.map(x => fmt(x.wind, 3)).join(', ')} m/s` : 'stable at all points', '', ub.length ? 'bad' : 'good');
      const dpm = ir.filter(x => x.pitch.pm !== null && x.pitch_both.pm !== null).map(x => ({ w: x.wind, d: x.pitch_both.pm - x.pitch.pm }));
      if (dpm.length) { const wst = dpm.reduce((a, b) => (b.d < a.d ? b : a)); metric(ic.metrics, 'Pitch-loop PM change', `${wst.d >= 0 ? '+' : ''}${fmt(wst.d, 3)}°`, `worst, @ ${fmt(wst.w, 3)} m/s, when the torque loop also acts`, wst.d < -10 ? 'bad' : ''); }
      const cps = ir.filter(x => x.coupling_db !== null);
      if (cps.length) { const c = cps.reduce((a, b) => (Math.abs(b.coupling_db) > Math.abs(a.coupling_db) ? b : a)); metric(ic.metrics, 'Coupling at pitch crossover', `${c.coupling_db >= 0 ? '+' : ''}${fmt(c.coupling_db, 3)} dB`, `largest, @ ${fmt(c.wind, 3)} m/s`); }
      if (ir.some(x => x.n_origin > 0)) h('div', 'notes', ic.extra, 'With both PI loops on the same speed error the closed loop keeps one pole at the origin: the two integrators can drift against each other. ROSCO\'s set-point smoother decides which loop holds the speed; it is not part of this linear model.');
      const IW = ir.map(x => x.wind);
      chartIn(ic, { series: [
        { name: 'Pitch loop alone', x: IW, y: ir.map(x => x.pitch.pm), color: S(1), marker: 'circle', size: 3.2, unit: 'deg' },
        { name: 'Pitch loop, torque loop also closed', x: IW, y: ir.map(x => x.pitch_both.pm), color: S(1), dash: 'dash', marker: 'square', size: 3, unit: 'deg' },
        { name: 'Torque loop alone', x: IW, y: ir.map(x => x.torque.pm), color: S(2), marker: 'circle', size: 3.2, unit: 'deg' },
        { name: 'Torque loop, pitch loop also closed', x: IW, y: ir.map(x => x.torque_both.pm), color: S(2), dash: 'dash', marker: 'square', size: 3, unit: 'deg' }],
        xlabel: 'Wind speed [m/s]', ylabel: 'Phase margin [deg]', xname: 'Wind', xunit: 'm/s', height: 280,
        annotations: [vl, { type: 'hline', y: 45, label: '45°', color: cssv('--muted') }] });
      const iwrap = h('div', 'tbl-wrap', ic.el);
      const itb = h('table', 'tbl', iwrap);
      itb.innerHTML = '<thead><tr><th>Wind [m/s]</th><th>Gen speed / rated</th><th class="l">Loop in use</th><th>Pitch alone GM / PM</th><th>Pitch, both closed GM / PM</th><th>Torque alone PM</th><th>Torque, both closed PM</th><th>Coupling [dB]</th><th class="l">Both closed</th></tr></thead>';
      const ib = h('tbody', null, itb);
      const gp = (m) => `${m.gm_db === null ? '∞' : fmt(m.gm_db, 3)} / ${fmt(m.pm, 3)}`;
      ir.forEach(x => {
        const tr = h('tr', 'click' + (x.op === st.op ? ' sel' : ''), ib);
        h('td', 'num', tr, fmt(x.wind, 3)); h('td', 'num', tr, x.speed_ratio === null ? '–' : fmt(x.speed_ratio, 3));
        h('td', 'l', tr, x.auto === 'pitch' ? 'pitch (R3)' : 'torque (R2)');
        h('td', 'num', tr, gp(x.pitch)); h('td', 'num', tr, gp(x.pitch_both)); h('td', 'num', tr, fmt(x.torque.pm, 3)); h('td', 'num', tr, fmt(x.torque_both.pm, 3));
        h('td', 'num', tr, x.coupling_db === null ? '–' : fmt(x.coupling_db, 3));
        const sd = h('td', 'l', tr, x.both_stable ? 'stable' : 'unstable'); sd.style.color = x.both_stable ? cssv('--good-ink') : cssv('--crit-ink');
        tr.onclick = () => { st.op = x.op; renderChips(); renderPanel(); };
      });
    },

    'cl:loop': async (P) => {
      const r = await closedData(P);
      const w = r.w.map(wconv);
      P._insights = [(el) => loopInsights(el, r)];
      const bc = card(P, 'Loop transfer function L(jω)', `L = −K·G broken at the ${r.act} input · ${fmt(r.wind, 3)} m/s · ${r.region_name}`);
      clMetrics(bc.metrics, r);
      const ann = [];
      (r.pm || []).forEach(p => { const best = r.pm_min && p.w === r.pm_min.w; ann.push({ type: 'vline', x: wconv(p.w), color: S(3), label: best ? `PM ${fmt(p.pm, 3)}°` : null, dash: best ? 'dash' : 'dot', width: best ? 1.4 : 0.8 }); });
      (r.gm || []).forEach(g => { const best = r.gm_min && g.w === r.gm_min.w; ann.push({ type: 'vline', x: wconv(g.w), color: S(2), label: best ? `GM ${fmt(g.gm_db, 3)} dB` : null, dash: best ? 'dash' : 'dot', dy: 14, width: best ? 1.4 : 0.8 }); });
      bc.chart = new LB.Chart(bc.host, { title: 'Loop magnitude', series: [{ name: '|L|', x: w, y: r.L_mag.map(db), fx: fxFreq(r.L_modal, 'db'), color: S(1), unit: 'dB' }], xlog: true, ylabel: 'Magnitude [dB]', xname: 'ω', xunit: wunit(), height: 270,
        annotations: ann.concat([{ type: 'hline', y: 0, color: cssv('--muted'), dash: 'solid', width: 1 }]) });
      const hostPh = h('div', null, bc.el);
      const ph = new LB.Chart(hostPh, { title: 'Loop phase', series: [{ name: '∠L', x: w, y: r.L_phase, fx: fxFreq(r.L_modal, 'phase'), color: S(1), unit: 'deg' }], xlog: true, xlabel: wlabel(), ylabel: 'Phase [deg]', xname: 'ω', xunit: wunit(), height: 220,
        annotations: ann.map(a => Object.assign({}, a, { label: null })).concat(phaseRefs(r.L_phase)) });
      LB.linkX([bc.chart, ph]);
      const sc = card(P, 'Sensitivity and complementary sensitivity', 'S = 1/(1+L) · T = L/(1+L)');
      metric(sc.metrics, '‖S‖∞', fmt(db(r.Smax), 3) + ' dB', `@ ${fw(r.w_Smax)}`, r.Smax > 2 ? 'bad' : '');
      metric(sc.metrics, '‖T‖∞', fmt(db(r.Tmax), 3) + ' dB', `@ ${fw(r.w_Tmax)}`);
      metric(sc.metrics, 'Bandwidth of T', r.bw_T ? fw(r.bw_T) : '–');
      chartIn(sc, { series: [{ name: '|S|', x: w, y: r.S_mag.map(db), fx: fxFreq(r.L_modal, 'S'), color: S(1), unit: 'dB' }, { name: '|T|', x: w, y: r.T_mag.map(db), fx: fxFreq(r.L_modal, 'T'), color: S(2), unit: 'dB' }],
        xlog: true, xlabel: wlabel(), ylabel: 'Magnitude [dB]', xname: 'ω', xunit: wunit(), height: 300,
        annotations: [{ type: 'hline', y: 0, color: cssv('--muted'), dash: 'solid', width: 1 }, { type: 'hline', y: 6, color: cssv('--muted'), label: '6 dB' }] });
    },

    'cl:nyquist': async (P) => {
      const r = await closedData(P);
      const c = card(P, 'Nyquist diagram', `L(jω) for ω from 0⁺ to ∞ (solid) and its mirror for ω < 0 (dashed) · ${fmt(r.wind, 3)} m/s`);
      clMetrics(c.metrics, r, true);
      const n = r.nyq_re.length;
      const at = [];
      for (let k = 1; k < 9; k++) at.push(Math.floor(n * (k / 9)));
      // extra arrows where the curve passes close to -1
      let imin = 0, dmin = Infinity;
      for (let i = 0; i < n; i++) { const d = Math.hypot(r.nyq_re[i] + 1, r.nyq_im[i]); if (d < dmin) { dmin = d; imin = i; } }
      at.push(imin);
      const tips = r.nyq_w.map((w, i) => `ω ${fmt(wconv(w), 4)} ${wunit()}<br>L = ${fmt(r.nyq_re[i], 4)} ${r.nyq_im[i] >= 0 ? '+' : '−'} ${fmt(Math.abs(r.nyq_im[i]), 4)}j<br>|1+L| ${fmt(Math.hypot(1 + r.nyq_re[i], r.nyq_im[i]), 4)}`);
      const series = [
        { name: 'L(jω), ω > 0', x: r.nyq_re, y: r.nyq_im, color: S(1), width: 2, arrows: { at }, sortedX: false, tips },
        { name: 'ω < 0 (mirror)', x: r.nyq_re, y: r.nyq_im.map(v => -v), color: S(1), width: 1.1, dash: 'dash', opacity: 0.35, sortedX: false, nohover: true },
      ];
      const ann = [
        { type: 'circle', cx: 0, cy: 0, r: 1, color: cssv('--muted'), dash: 'dash', width: 1 },
        { type: 'circle', cx: -1, cy: 0, r: r.s_m, color: S(8), width: 1.2, fill: S(8), fillOpacity: 0.06 },
        { type: 'point', x: -1, y: 0, marker: 'x', color: S(8), size: 6, label: '−1', tip: '<b>Critical point −1</b>' },
        { type: 'point', x: r.nyq_re[0], y: r.nyq_im[0], color: S(1), size: 4.5, label: 'ω → 0⁺', tip: 'start of the curve (ω → 0⁺)', fit: false },
      ];
      (r.gm || []).forEach(g => { const best = r.gm_min && Math.abs(g.w - r.gm_min.w) < 1e-12; ann.push({ type: 'point', x: -1 / g.gm, y: 0, color: S(2), marker: 'diamond', size: best ? 5.5 : 4, label: best ? `GM ${fmt(g.gm_db, 3)} dB` : null, dy: 16, tip: `<b>Phase crossover</b><br>${fw(g.w)} · GM ${fmt(g.gm_db, 3)} dB`, fit: false }); });
      (r.pm || []).forEach(p => { const best = r.pm_min && Math.abs(p.w - r.pm_min.w) < 1e-12; ann.push({ type: 'point', x: p.re, y: p.im, color: S(3), marker: 'diamond', size: best ? 5.5 : 4, label: best ? `PM ${fmt(p.pm, 3)}°` : null, tip: `<b>Gain crossover</b><br>${fw(p.w)} · PM ${fmt(p.pm, 3)}°`, fit: false }); });
      const opts = { series, xlabel: 'Re L', ylabel: 'Im L', xname: 'Re', height: 520, equal: true, xzeroline: true, annotations: ann, hover: 'nearest' };
      const ch = chartIn(c, opts);
      // default view: around the critical point
      ch.view = { x: [-3, 1.5], y: [-2.2, 2.2] }; ch.render();
      const tools = c.tools;
      const b = h('button', 'tool', tools, 'Fit all'); tools.insertBefore(b, tools.firstChild);
      b.onclick = () => { ch.view = null; ch.render(); };
      const b2 = h('button', 'tool', tools, 'Near −1'); tools.insertBefore(b2, tools.firstChild);
      b2.onclick = () => { ch.view = { x: [-3, 1.5], y: [-2.2, 2.2] }; ch.render(); };
    },

    'cl:poles': async (P) => {
      const r = await closedData(P);
      const fcol = FAMCOL();
      const fmax = st.poleFmax || 0.6;
      const ctr = h('div', 'ctrls', P);
      const f2 = h('label', 'fl', ctr); h('span', 'lab', f2, 'Max frequency [Hz]');
      const fx = h('input', null, f2); fx.type = 'number'; fx.step = '0.1'; fx.value = fmax; fx.onchange = () => { st.poleFmax = +fx.value; renderPanel(); };
      const f3 = h('label', 'chk', ctr); const cb = h('input', null, f3); cb.type = 'checkbox'; cb.checked = !!st.showHydro; f3.appendChild(document.createTextNode(' show hydrodynamic state poles'));
      cb.onchange = () => { st.showHydro = cb.checked; renderPanel(); };
      const hyd = (lab) => /\(hydro\)|^HD |^AD /.test(lab);
      const cl = r.cl_poles.filter(p => p.fn <= fmax && (st.showHydro || !hyd(p.label)));
      const ol = r.ol_poles.filter(p => p.fn <= fmax && p.im >= 0 && (st.showHydro || !hyd(p.label)));
      const row = h('div', 'row2', P);
      const pc = card(row, 'Closed-loop poles', `filled: closed loop · hollow: open loop · ${fmt(r.wind, 3)} m/s`);
      const ctrlCol = S(7);
      const colOf = (p) => (p.origin === 'controller' ? ctrlCol : (fcol[famOfLabel(p.label)] || S(8)));
      const series = [
        { name: 'Open loop', type: 'scatter', x: ol.map(p => p.re), y: ol.map(p => p.im), color: cssv('--muted'), hollow: true, size: 4.2,
          tips: ol.map(p => `<b>Open loop</b> · ${p.label}<br>f<sub>n</sub> ${fmt(p.fn, 4)} Hz · ζ ${fmt(p.zeta * 100, 3)}%`) },
        { name: 'Closed loop', type: 'scatter', x: cl.map(p => p.re), y: cl.map(p => p.im), color: S(1), colors: cl.map(colOf), size: 4.8,
          markers: cl.map(p => p.origin === 'controller' ? 'square' : 'circle'),
          tips: cl.map(p => `<b>${p.label}</b> (${p.origin})<br>f<sub>n</sub> ${fmt(p.fn, 4)} Hz · ζ ${fmt(p.zeta * 100, 3)}%<br>${p.top.map(([a, b]) => `${a} ${fmt(b * 100, 2)}%`).join('<br>')}`) },
      ];
      const maxim = Math.max(0.2, ...cl.map(p => p.im), ...ol.map(p => p.im));
      const zl = [0.05, 0.2, 0.5].map(z => ({ name: `ζ ${z}`, x: [0, -z * maxim / Math.sqrt(1 - z * z)], y: [0, maxim], color: cssv('--grid'), width: 1, dash: 'dash', nolegend: true, nohover: true, noextent: true }));
      const osc = cl.filter(p => p.im > 1e-9);
      if (osc.length) { const lz = osc.reduce((a, b) => (b.zeta < a.zeta ? b : a)); metric(pc.metrics, 'Least damped', fmt(lz.zeta * 100, 3) + '%', lz.label, lz.zeta < 0.05 ? 'bad' : ''); }
      const rhp = r.cl_poles.filter(p => p.re > 1e-7);
      metric(pc.metrics, 'RHP poles', rhp.length, '', rhp.length ? 'bad' : 'good');
      metric(pc.metrics, 'Controller poles', r.cl_poles.filter(p => p.origin === 'controller').length);
      chartIn(pc, { series: zl.concat(series), xlabel: 'Real part [1/s]', ylabel: 'Imaginary part [rad/s]', height: 420, hover: 'nearest', xzeroline: true, xinclude: 0, yinclude: 0 });
      const tc = card(row, 'Closed-loop modes', `f ≤ ${fmax} Hz · sorted by damping`, { nochart: true });
      const wrap = h('div', 'tbl-wrap', tc.el);
      const t = h('table', 'tbl', wrap);
      t.innerHTML = '<thead><tr><th>f<sub>n</sub> [Hz]</th><th>ω<sub>n</sub> [rad/s]</th><th>ζ [%]</th><th class="l">Dominant state</th><th class="l">Origin</th><th class="l">Participation</th></tr></thead>';
      const tb = h('tbody', null, t);
      const sorted = cl.slice().sort((a, b) => a.zeta - b.zeta);
      const dom = r.summary && r.summary.speed_pole;
      sorted.forEach((p, i) => {
        const tr = h('tr', null, tb);
        if (i === 0 || (dom && Math.abs(dom.re - p.re) < 1e-9 && Math.abs(dom.im - Math.abs(p.im)) < 1e-9)) tr.classList.add('sel');
        h('td', 'num', tr, fmt(p.fn, 4)); h('td', 'num', tr, fmt(p.wn, 4));
        const tz = h('td', 'num', tr, fmt(p.zeta * 100, 3)); if (p.zeta < 0) tz.style.color = cssv('--crit-ink');
        h('td', 'l', tr, p.label); h('td', 'l', tr, p.origin);
        h('td', 'l', tr, p.top.slice(0, 3).map(([a, b]) => `${a} ${fmt(b * 100, 2)}%`).join(' · '));
      });
      // root locus
      const rc = card(P, 'Root locus', 'closed-loop poles as the loop gain is scaled by k (k = 1 is the DISCON gain)');
      const ks = r.root_locus.map(x => x.k), cols = seqRamp(ks.length);
      const xs = [], ys = [], cc = [], tips = [], sizes = [];
      r.root_locus.forEach((x, i) => x.re.forEach((re, j) => { const im = x.im[j]; if (!st.showHydro && Math.abs(im) > 0 && false) return; xs.push(re); ys.push(im); cc.push(Math.abs(x.k - 1) < 1e-9 ? S(2) : cols[i]); sizes.push(Math.abs(x.k - 1) < 1e-9 ? 4.5 : 2.4); tips.push(`k = ${fmt(x.k, 3)}<br>${fmt(re, 4)} + ${fmt(im, 4)}j<br>f<sub>n</sub> ${fmt(Math.hypot(re, im) / TWO_PI, 4)} Hz · ζ ${fmt(-re / Math.hypot(re, im) * 100, 3)}%`); }));
      const kc = ks.map((k, i) => ({ k, i })).filter(o => Math.abs(o.k - 1) < 1e-9);
      metric(rc.metrics, 'Gain range', `${fmt(ks[0], 2)} … ${fmt(ks[ks.length - 1], 3)}`);
      const unstK = r.root_locus.find(x => x.re.some(v => v > 1e-7));
      metric(rc.metrics, 'First unstable k', unstK ? fmt(unstK.k, 3) : `> ${fmt(ks[ks.length - 1], 3)}`, '', unstK && unstK.k <= 1 ? 'bad' : '');
      chartIn(rc, { series: [{ name: 'poles', type: 'scatter', x: xs, y: ys, colors: cc, color: cols[cols.length - 1], size: 2.4, sizes, tips }], xlabel: 'Real part [1/s]', ylabel: 'Imaginary part [rad/s]', height: 420, hover: 'nearest', xzeroline: true,
        colorbar: { label: 'loop gain factor k', min: ks[0], max: ks[ks.length - 1], colors: cols }, legend: false, xdomain: [null, Math.max(0.05, ...xs.filter(v => v < 5)) * 1.1] });
      void kc;
    },

    // ---------------------------------------------------------------- gain study
    'cl:gains': async (P) => {
      const s = st.summary, G = st.gs, op = st.op;
      const ctr = h('div', 'ctrls', P);
      const seg = (label, opts, cur, set) => {
        const f = h('div', 'fl', ctr); h('span', 'lab', f, label);
        const sg = h('div', 'seg small', f);
        opts.forEach(([v, lab]) => { const b = h('button', cur === v ? 'on' : '', sg, lab); b.onclick = () => { set(v); renderPanel(); }; });
      };
      const hasFl = (s.closed || []).some(c => c.fl_mode);
      seg('Gains varied', [['pi', 'Kp × Ki']].concat(hasFl ? [['float', 'PI × floating']] : []), G.pair, v => { G.pair = v; });
      seg('Range', [['0.5-2', '×0.5 – ×2'], ['0.25-4', '×0.25 – ×4'], ['0.1-10', '×0.1 – ×10']], G.range, v => { G.range = v; });
      seg('Resolution', [[15, '15²'], [25, '25²'], [41, '41²']], G.n, v => { G.n = v; });
      seg('Colour by', [['sp', 'speed-loop damping'], ['min', 'least-damped mode']], G.color, v => { G.color = v; });
      const ft = h('label', 'fl', ctr); h('span', 'lab', ft, 'Contour at ζ');
      const ti = h('input', null, ft); ti.type = 'number'; ti.step = '0.05'; ti.min = '0'; ti.value = G.target;
      ti.onchange = () => { G.target = +ti.value; renderPanel(); };
      const [lo, hi] = G.range.split('-').map(Number);
      const wait = loading(P, `Closing the loop ${G.n * G.n} times at ${fmt(s.ops[op].wind, 3)} m/s…`);
      const scaleP = api('/api/gain_scaling', {}).catch(e => ({ failed: e }));
      const r = await api('/api/gain_map', { op, pair: G.pair, lo, hi, n: G.n });
      wait.remove();
      const nm = r.names, isPI = G.pair === 'pi', key = op + ':' + G.pair;
      const xn = isPI ? nm.kp_name : 'PI', yn = isPI ? nm.ki_name : 'Kp_float';
      const Zc = G.color === 'sp' ? r.zeta_sp : r.zeta;
      const unst = r.max_re.map(row => row.map(v => v > 1e-7));
      const nU = unst.flat().filter(Boolean).length;

      // ---- stability map
      const mc = card(P, 'Stability map', `${r.region_name} · ${fmt(r.wind, 3)} m/s · each cell = one closed loop · click a cell to try those gains`);
      metric(mc.metrics, 'DISCON gains', r.base.stable ? 'stable' : 'unstable', '1 × 1 on the map', r.base.stable ? 'good' : 'bad');
      metric(mc.metrics, nm.kp_name, fmt(nm.kp, 4), nm.kp_unit);
      metric(mc.metrics, nm.ki_name, fmt(nm.ki, 4), nm.ki_unit);
      if (r.base.wn_sp) metric(mc.metrics, 'Speed-loop pole', `${fmt(r.base.wn_sp, 3)} rad/s`, `ζ ${fmt(r.base.zeta_sp, 3)}`);
      metric(mc.metrics, 'Least-damped moved mode', r.base.zeta === null ? '–' : fmt(r.base.zeta * 100, 3) + ' %', r.base.fn ? `@ ${fmt(r.base.fn, 3)} Hz` : '');
      metric(mc.metrics, 'Unstable settings', `${nU} / ${G.n * G.n}`, '', nU ? 'bad' : 'good');
      zLegend(mc.extra, G.color === 'sp' ? 'colour = damping of the speed-loop pole · red = some closed-loop pole unstable' : 'colour = least-damped closed-loop pole the gains can move');
      if (r.unaffected && r.unaffected.length) h('div', 'notes', mc.extra, 'Not moved by these gains (the loop cannot excite or see them): ' + r.unaffected.map(u => `${u.label} (${fmt(u.fn, 3)} Hz, ζ ${fmt(u.zeta * 100, 3)} %)`).join(' · '));
      const tipCell = (ix, iy) => `<b>${xn} ×${fmt(r.fx[ix], 3)} · ${yn} ×${fmt(r.fy[iy], 3)}</b><br>` +
        (unst[iy][ix] ? `<b style="color:var(--crit-ink)">unstable</b> · max Re λ = ${fmt(r.max_re[iy][ix], 3)} 1/s` : 'stable') +
        `<br>speed loop: ω<sub>n</sub> ${fmt(r.wn_sp[iy][ix], 3)} rad/s · ζ ${fmt(r.zeta_sp[iy][ix], 3)}` +
        `<br>least-damped moved pole: ζ ${fmt(r.zeta[iy][ix] * 100, 3)} % @ ${fmt(r.fn[iy][ix], 3)} Hz`;
      const bound = contour(r.fx, r.fy, r.max_re, 1e-7, true, true);
      const tgt = contour(r.fx, r.fy, Zc, G.target, true, true);
      const series = [
        { name: 'map', type: 'heatmap', nolegend: true, xs: r.fx, ys: r.fy, z: Zc, color: (v, ix, iy) => zColor(v, unst[iy][ix]), tip: tipCell },
        { name: 'Stability boundary', x: bound.x, y: bound.y, color: cssv('--ink'), width: 2.4, nohover: true, noextent: true },
        { name: `ζ = ${G.target}`, x: tgt.x, y: tgt.y, color: cssv('--ink'), width: 1.4, dash: 'dash', nohover: true, noextent: true },
      ];
      const marks = () => {
        const t = G.trial[key] || { fx: 1, fy: 1 };
        const a = [{ type: 'point', x: 1, y: 1, marker: 'diamond', color: cssv('--ink'), size: 6, label: 'DISCON', tip: `<b>DISCON gains</b><br>${nm.kp_name} ${fmt(nm.kp, 4)} · ${nm.ki_name} ${fmt(nm.ki, 4)}` }];
        if (r.target && isPI) a.push({ type: 'point', x: r.target.fx, y: r.target.fy, marker: 'square', color: S(3), size: 5, label: 'YAML tuning', tip: `<b>YAML tuning at this pitch</b><br>Kp ${fmt(r.target.kp, 4)} · Ki ${fmt(r.target.ki, 4)}` });
        if (Math.abs(t.fx - 1) > 1e-9 || Math.abs(t.fy - 1) > 1e-9) a.push({ type: 'point', x: t.fx, y: t.fy, marker: 'circle', color: S(8), size: 6, label: 'trial', tip: `<b>Trial</b><br>${xn} ×${fmt(t.fx, 3)} · ${yn} ×${fmt(t.fy, 3)}` });
        return a;
      };
      const ch = chartIn(mc, { series, xlog: true, ylog: true, xlabel: isPI ? `${nm.kp_name} / DISCON value` : 'PI gains (Kp and Ki) / DISCON value',
        ylabel: isPI ? `${nm.ki_name} / DISCON value` : 'Kp_float / DISCON value', xname: xn + ' ×', yname: yn + ' ×', height: 460, legend: true,
        annotations: marks(), onClick: (x, y) => { G.trial[key] = { fx: x, fy: y }; ch.set({ annotations: marks() }); renderTrial(); } });

      // ---- trial gains
      const TR = h('div', null, P);
      async function renderTrial() {
        const t = G.trial[key] || { fx: 1, fy: 1 };
        TR.innerHTML = '';
        const w8 = loading(TR, 'Analysing the trial gains (margins, poles, responses)…');
        let p;
        try { p = await api('/api/gain_point', { op, fx: t.fx, fy: t.fy, pair: G.pair }); } catch (e) { w8.remove(); errorBox(TR, e); return; }
        w8.remove();
        const B = p.base, T = p.trial;
        const tc = card(TR, 'Trial gains vs DISCON', `${fmt(p.wind, 3)} m/s · ${p.region === 'pitch' ? 'pitch' : 'torque'} loop`, { nochart: true });
        const form = h('div', 'ctrls', tc.extra);
        const inp = (label, v) => { const f = h('label', 'fl', form); h('span', 'lab', f, label); const i = h('input', null, f); i.type = 'number'; i.step = 'any'; i.value = +v.toPrecision(4); return i; };
        const ix = inp(`${xn} factor`, t.fx), iy = inp(`${yn} factor`, t.fy);
        const go = h('button', 'btn primary', form, 'Analyse');
        go.onclick = () => { const fx = +ix.value, fy = +iy.value; if (fx > 0 && fy > 0) { G.trial[key] = { fx, fy }; ch.set({ annotations: marks() }); renderTrial(); } };
        const rs = h('button', 'btn', form, 'Back to DISCON');
        rs.onclick = () => { delete G.trial[key]; ch.set({ annotations: marks() }); renderTrial(); };
        const tb = h('table', 'cmp', tc.el);
        tb.innerHTML = '<thead><tr><th></th><th>DISCON</th><th>Trial</th><th>Change</th></tr></thead>';
        const body = h('tbody', null, tb);
        const row = (lab, a, b, f, good) => {
          const tr = h('tr', null, body); h('td', null, tr, lab);
          const ta = h('td', null, tr, f(a)), tb2 = h('td', null, tr, f(b));
          const d = (typeof a === 'number' && typeof b === 'number' && isFinite(a) && isFinite(b)) ? b - a : null;
          h('td', null, tr, d === null ? '' : (d > 0 ? '+' : '') + f(d));
          if (good) { const ga = good(a), gb = good(b); if (ga === false) ta.className = 'bad'; if (gb === false) tb2.className = 'bad'; if (gb === true && ga === false) tb2.className = 'good'; }
        };
        const n3 = (v) => (v === null || v === undefined ? '–' : fmt(v, 4));
        row(nm.kp_name + ` [${nm.kp_unit}]`, B.kp, T.kp, n3);
        row(nm.ki_name + ` [${nm.ki_unit}]`, B.ki, T.ki, n3);
        if (G.pair === 'float') row('Kp_float [s]', B.kp_float, T.kp_float, n3);
        const stab = (v) => (v ? 'stable' : 'unstable');
        const tr0 = h('tr', null, body); h('td', null, tr0, 'Closed loop');
        [B, T].forEach(x => { const td = h('td', x.stable ? 'good' : 'bad', tr0, stab(x.stable) + (x.stable ? '' : ` (${x.Z} RHP)`)); void td; }); h('td', null, tr0, '');
        row('Gain margin [dB]', B.gm && B.gm.gm_db, T.gm && T.gm.gm_db, n3, v => (v === null || v === undefined ? null : v >= 6));
        row('Phase margin [deg]', B.pm && B.pm.pm, T.pm && T.pm.pm, n3, v => (v === null || v === undefined ? null : v >= 30));
        row('Stability margin s_m = min|1+L|', B.s_m, T.s_m, n3, v => v >= 0.4);
        row('Delay margin [s]', B.dm, T.dm, n3);
        row('‖S‖∞ [dB]', db(B.Smax), db(T.Smax), n3, v => v <= 8);
        row('Bandwidth of T [rad/s]', B.bw_T, T.bw_T, n3);
        row('Speed-loop ωn [rad/s]', B.wn_sp, T.wn_sp, n3);
        row('Speed-loop ζ', B.zeta_sp, T.zeta_sp, n3, v => (v === null || v === undefined ? null : v >= 0.3));
        row('Least-damped moved pole ζ [%]', B.zeta_min === null ? null : B.zeta_min * 100, T.zeta_min === null ? null : T.zeta_min * 100, n3, v => (v === null ? null : v > 0));
        h('div', 'muted', tc.el, 'Guides used for colouring: GM ≥ 6 dB, PM ≥ 30°, s_m ≥ 0.4, ‖S‖∞ ≤ 8 dB, speed-loop ζ ≥ 0.3.');

        const g1 = h('div', 'row2', TR);
        // poles
        const pc = card(g1, 'Closed-loop poles', 'hollow: DISCON · filled: trial · f ≤ 1 Hz');
        const pser = (x, name, hollow, col) => ({ name, type: 'scatter', x: x.poles.map(q => q.re), y: x.poles.map(q => q.im), color: col, hollow, size: hollow ? 5 : 4.2,
          tips: x.poles.map(q => `<b>${name}</b> · ${q.label}<br>f<sub>n</sub> ${fmt(q.fn, 4)} Hz · ζ ${fmt(q.zeta * 100, 3)} %`) });
        chartIn(pc, { series: [pser(B, 'DISCON', true, cssv('--muted')), pser(T, 'Trial', false, S(8))], xlabel: 'Real part [1/s]', ylabel: 'Imaginary part [rad/s]',
          height: 360, hover: 'nearest', xzeroline: true, xinclude: 0, yinclude: 0 });
        // nyquist
        const nc = card(g1, 'Nyquist near −1', 'L(jω) for ω > 0 · dashed: DISCON · solid: trial');
        const nyq = (m, name, col, dash) => {
          if (!m) return null;
          const w = wgrid(1e-3, 40, 1500, [m]), Gw = MOD.freq(m, w);
          const x = [], y = [], tips = [];
          for (let i = 0; i < w.length; i++) { if (Math.hypot(Gw.re[i], Gw.im[i]) > 1e3) continue; x.push(Gw.re[i]); y.push(Gw.im[i]); tips.push(`${name} · ω ${fmt(w[i], 4)} rad/s<br>|1+L| ${fmt(Math.hypot(1 + Gw.re[i], Gw.im[i]), 3)}`); }
          return { name, x, y, color: col, dash, sortedX: false, width: dash ? 1.4 : 2, tips };
        };
        const ns = [nyq(B.L_modal, 'DISCON', cssv('--muted'), 'dash'), nyq(T.L_modal, 'Trial', S(8), null)].filter(Boolean);
        const nch = chartIn(nc, { series: ns, xlabel: 'Re L', ylabel: 'Im L', height: 360, equal: true, xzeroline: true, hover: 'nearest',
          annotations: [{ type: 'circle', cx: 0, cy: 0, r: 1, color: cssv('--muted'), dash: 'dash', width: 1 },
            { type: 'circle', cx: -1, cy: 0, r: T.s_m, color: S(8), width: 1, fill: S(8), fillOpacity: 0.06 },
            { type: 'point', x: -1, y: 0, marker: 'x', color: cssv('--crit'), size: 6, label: '−1', tip: '<b>Critical point −1</b>' }] });
        nch.view = { x: [-3, 1.5], y: [-2.2, 2.2] }; nch.render();
        // step responses
        const g2 = h('div', 'row2', TR);
        const tser = (x, k, name, col, dash) => ({ name, x: x.t, y: x[k], fx: fxTime(x[k + '_modal'], 'step', 1), color: col, dash, width: dash ? 1.4 : 2 });
        const yc = card(g2, 'Step response: ' + shortLab(p.output.label), `1 ${p.input.unit} step on ${shortLab(p.input.label)} · closed loop`);
        chartIn(yc, { series: [tser(B, 'y', 'DISCON', cssv('--muted'), 'dash'), tser(T, 'y', 'Trial', S(8))], xlabel: 'Time [s]',
          ylabel: `${shortLab(p.output.label)} [${p.output.unit}]`, xname: 't', xunit: 's', height: 300 });
        const uc = card(g2, 'Step response: ' + p.act + ' command', `same step · actuator effort`);
        chartIn(uc, { series: [tser(B, 'u', 'DISCON', cssv('--muted'), 'dash'), tser(T, 'u', 'Trial', S(8))], xlabel: 'Time [s]',
          ylabel: `${p.act} [${p.act_unit}]`, xname: 't', xunit: 's', height: 300 });
      }
      await renderTrial();

      // ---- PI gain scaling across wind speeds
      const sc = await scaleP;
      if (sc.failed) { errorBox(P, sc.failed); return; }
      const rows = sc.rows, fs = sc.factors;
      const kc = card(P, 'PI gain scaling across wind speeds', 'both PI gains × k at every operating point (DISCON: k = 1) · click a cell to open it above');
      const zk = G.color === 'sp' ? 'zeta_sp' : 'zeta';
      const Zs = fs.map((_, iy) => rows.map(rw => rw[zk][iy]));
      const Us = fs.map((_, iy) => rows.map(rw => !rw.stable[iy]));
      const Wn = rows.map(rw => rw.wind);
      const his = rows.filter(rw => rw.k_hi !== null), los = rows.filter(rw => rw.k_lo !== null);
      const worst = his.length ? his.reduce((a, b) => (b.k_hi < a.k_hi ? b : a)) : null;
      if (worst) metric(kc.metrics, 'Smallest gain headroom', `×${fmt(worst.k_hi, 3)}`, `@ ${fmt(worst.wind, 3)} m/s (${fmt(20 * Math.log10(worst.k_hi), 3)} dB)`, worst.k_hi < 2 ? 'bad' : '');
      const unstNow = rows.filter(rw => !rw.stable_1);
      metric(kc.metrics, 'Unstable at DISCON gains', unstNow.length ? unstNow.map(rw => fmt(rw.wind, 3)).join(', ') + ' m/s' : 'none', '', unstNow.length ? 'bad' : 'good');
      zLegend(kc.extra);
      chartIn(kc, { series: [
        { name: 'map', type: 'heatmap', nolegend: true, xs: Wn, ys: fs, z: Zs, color: (v, ix, iy) => zColor(v, Us[iy][ix]),
          tip: (ix, iy) => `<b>${fmt(Wn[ix], 3)} m/s · PI × ${fmt(fs[iy], 3)}</b><br>${Us[iy][ix] ? '<b style="color:var(--crit-ink)">unstable</b>' : 'stable'}<br>ζ ${fmt(Zs[iy][ix], 3)}<br>stable for k in ${rows[ix].k_lo === null ? '(<0.05' : '(' + fmt(rows[ix].k_lo, 3)}, ${rows[ix].k_hi === null ? '>20)' : fmt(rows[ix].k_hi, 3) + ')'}` },
        { name: 'Upper stability limit', x: his.map(rw => rw.wind), y: his.map(rw => rw.k_hi), color: cssv('--ink'), width: 2, marker: 'circle', size: 3, noextent: true, nohover: true },
        { name: 'Lower stability limit', x: los.map(rw => rw.wind), y: los.map(rw => rw.k_lo), color: cssv('--ink'), width: 2, dash: 'dash', marker: 'square', size: 3, noextent: true, nohover: true }],
        ylog: true, xlabel: 'Wind speed [m/s]', ylabel: 'PI gain factor k', xname: 'Wind', yname: 'k', height: 380,
        annotations: [{ type: 'hline', y: 1, color: cssv('--ink'), dash: 'dot', label: 'DISCON', width: 1.2 }, { type: 'vline', x: s.ops[op].wind, color: cssv('--accent'), dash: 'solid', width: 1 }],
        onClick: (x, y) => {
          let best = 0; Wn.forEach((w, i) => { if (Math.abs(w - x) < Math.abs(Wn[best] - x)) best = i; });
          const o2 = rows[best].op; G.pair = 'pi'; G.trial[o2 + ':pi'] = { fx: y, fy: y }; st.op = o2; renderChips(); renderPanel();
        } });
      const tw = h('div', 'tbl-wrap', kc.el);
      const t = h('table', 'tbl', tw);
      t.innerHTML = '<thead><tr><th>Wind [m/s]</th><th class="l">Loop</th><th>Kp</th><th>Ki</th><th>Stable for k from</th><th>to</th><th>Gain headroom [dB]</th></tr></thead>';
      const tbd = h('tbody', null, t);
      rows.forEach(rw => {
        const tr = h('tr', 'click' + (rw.op === op ? ' sel' : ''), tbd);
        h('td', 'num', tr, fmt(rw.wind, 3)); h('td', 'l', tr, rw.region === 'pitch' ? 'pitch PI' : 'torque PI');
        h('td', 'num', tr, fmt(rw.kp, 4)); h('td', 'num', tr, fmt(rw.ki, 4));
        if (!rw.stable_1) { const td = h('td', 'l', tr, 'unstable at the DISCON gains'); td.colSpan = 3; td.style.color = cssv('--crit-ink'); }
        else {
          h('td', 'num', tr, rw.k_lo === null ? '< 0.05' : fmt(rw.k_lo, 3)); h('td', 'num', tr, rw.k_hi === null ? '> 20' : fmt(rw.k_hi, 3));
          const hd = h('td', 'num', tr, rw.k_hi === null ? '> 26' : fmt(20 * Math.log10(rw.k_hi), 3)); if (rw.k_hi !== null && rw.k_hi < 2) hd.style.color = cssv('--crit-ink');
        }
        tr.onclick = () => { st.op = rw.op; renderChips(); renderPanel(); };
      });
    },

    'cl:cltime': async (P) => {
      const s = st.summary, ch = s.channels, T = st.cio;
      const ctr = h('div', 'ctrls', P);
      channelSelect(ctr, 'Disturbance (plant input)', ch.inputs, T.input, v => { T.input = v; T.amp = null; renderPanel(); }, inputOk);
      const outF = h('label', 'fl', ctr); h('span', 'lab', outF, 'Output');
      const oseg = h('div', 'seg small', outF);
      [['plant', 'plant output'], ['cmd', 'controller command']].forEach(([v, lab]) => { const b = h('button', T.out_kind === v ? 'on' : '', oseg, lab); b.onclick = () => { T.out_kind = v; renderPanel(); }; });
      if (T.out_kind === 'plant') channelSelect(ctr, 'Plant output', ch.outputs, T.output, v => { T.output = v; renderPanel(); }, outputOk);
      const inp = ch.inputs.find(c => c.i === T.input);
      const f1 = h('label', 'fl', ctr); h('span', 'lab', f1, 'Signal');
      const seg = h('div', 'seg small', f1);
      ['step', 'impulse'].forEach(k => { const b = h('button', T.kind === k ? 'on' : '', seg, k); b.onclick = () => { T.kind = k; $$('button', seg).forEach(x => x.classList.toggle('on', x === b)); }; });
      const f2 = h('label', 'fl', ctr); h('span', 'lab', f2, `Amplitude [${inp ? inp.disp_unit : ''}]`);
      const amp = h('input', null, f2); amp.type = 'number'; amp.step = 'any';
      if (T.amp === null || T.amp === undefined) T.amp = defaultAmp(inp);
      amp.value = T.amp;
      const f3 = h('label', 'fl', ctr); h('span', 'lab', f3, 'Duration [s]');
      const dur = h('input', null, f3); dur.type = 'number'; dur.placeholder = 'auto'; dur.value = T.T;
      const go = h('button', 'btn primary', ctr, 'Generate');
      const c = card(P, 'Closed- vs open-loop response', '');
      const fc = card(P, 'Disturbance rejection', 'closed- vs open-loop frequency response from the same input');
      const run = async () => {
        T.amp = +amp.value; T.T = dur.value;
        c.metrics.innerHTML = ''; c.host.innerHTML = '';
        const wait = loading(c.host);
        const r = await api('/api/closed_time', { op: st.op, input: T.input, output: T.output, kind: T.kind, amp: T.amp, T: T.T || null, out_kind: T.out_kind });
        wait.remove();
        c.el.querySelector('.sub').textContent = `${T.kind} of ${fmt(T.amp, 4)} ${r.units.input} on ${r.labels.input} → ${r.labels.output} · ${fmt(r.wind, 3)} m/s`;
        const mc = r.metrics_cl, mo = r.metrics_ol;
        metric(c.metrics, 'Closed-loop peak', fmt(mc.peak, 4), `${r.units.output} @ ${fmt(mc.t_peak, 3)} s`);
        metric(c.metrics, 'Closed-loop final', mc.final === null ? 'not settled' : fmt(mc.final, 4), mc.final === null ? '' : r.units.output);
        if (T.kind === 'step' && mc.t_settle !== null) metric(c.metrics, 'Closed-loop settling 2%', fmt(mc.t_settle, 3) + ' s');
        if (mo) { metric(c.metrics, 'Open-loop peak', fmt(mo.peak, 4), `${r.units.output} @ ${fmt(mo.t_peak, 3)} s`); if (mo.peak && mc.peak) metric(c.metrics, 'Peak reduction', fmt((1 - Math.abs(mc.peak / mo.peak)) * 100, 3) + '%'); }
        const series = [{ name: 'Closed loop', x: r.t, y: r.y_cl, fx: fxTime(r.cl_modal, T.kind, T.amp), color: S(1), unit: r.units.output }];
        if (r.y_ol) series.push({ name: 'Open loop', x: r.t, y: r.y_ol, fx: fxTime(r.ol_modal, T.kind, T.amp), color: S(2), dash: 'dash', unit: r.units.output });
        chartIn(c, { series, xlabel: 'Time [s]', ylabel: `${shortLab(r.labels.output)} [${r.units.output}]`, xname: 't', xunit: 's', height: 360 });
        fc.metrics.innerHTML = ''; fc.host.innerHTML = '';
        if (T.out_kind === 'plant') {
          const w8 = loading(fc.host);
          const f = await api('/api/closed_tf', { op: st.op, input: T.input, output: T.output });
          w8.remove();
          const w = f.w.map(wconv);
          chartIn(fc, { series: [{ name: 'Closed loop', x: w, y: f.cl_mag.map(db), fx: fxFreq(f.cl_modal, 'db'), color: S(1), unit: 'dB' }, { name: 'Open loop', x: w, y: f.ol_mag.map(db), fx: fxFreq(f.ol_modal, 'db'), color: S(2), dash: 'dash', unit: 'dB' }],
            xlog: true, xlabel: wlabel(), ylabel: `Magnitude [dB ${f.units.output}/${f.units.input}]`, xname: 'ω', xunit: wunit(), height: 300 });
        } else fc.el.hidden = true;
      };
      go.onclick = () => run().catch(e => errorBox(c.host, e));
      await run();
    },

    'cl:ctrl': async (P) => {
      const s = st.summary, C = s.controller, d = s.design, r = await closedData(P);
      const row = h('div', 'row2', P);
      // gain schedule
      const gc = card(row, 'Pitch gain schedule', 'DISCON PC_GS_KP / PC_GS_KI vs blade pitch; markers: operating points');
      const ang = C.PC_GS_angles.map(deg);
      const opsP = s.closed.filter(x => x.region === 'pitch' && !x.error);
      const pitchOf = (i) => s.ops[i].pitch_deg;
      const ser = [{ name: 'Kp [s]', x: ang, y: C.PC_GS_KP, color: S(1) }, { name: 'Ki [–]', x: ang, y: C.PC_GS_KI, color: S(2) }];
      if (C.tuned) ser.push({ name: 'Kp from YAML tuning', x: C.tuned.PC_GS_angles.map(deg), y: C.tuned.pc_Kp, color: S(1), dash: 'dot', width: 1.5 }, { name: 'Ki from YAML tuning', x: C.tuned.PC_GS_angles.map(deg), y: C.tuned.pc_Ki, color: S(2), dash: 'dot', width: 1.5 });
      ser.push({ name: 'Operating points', type: 'scatter', x: opsP.map(x => pitchOf(x.op)), y: opsP.map(x => x.Kp), color: S(1), size: 3.5, nolegend: true, tips: opsP.map(x => `${fmt(x.wind, 3)} m/s · pitch ${fmt(pitchOf(x.op), 3)}°<br>Kp ${fmt(x.Kp, 4)} · Ki ${fmt(x.Ki, 4)}`) });
      metric(gc.metrics, 'Kp at this point', fmt(r.Kp, 4), 's'); metric(gc.metrics, 'Ki at this point', fmt(r.Ki, 4));
      if (r.fl_mode) metric(gc.metrics, 'Floating gain Kp_float', fmt(r.Kp_float, 4), 's');
      chartIn(gc, { series: ser, xlabel: 'Blade pitch [deg]', ylabel: 'Gain', xname: 'Pitch', xunit: 'deg', height: 300,
        annotations: [{ type: 'vline', x: s.ops[st.op].pitch_deg, color: cssv('--accent'), dash: 'solid', width: 1 }] });
      // controller bode
      const kc = card(row, 'Controller frequency response', `generator speed [rad/s] → ${r.act} [${r.act_unit}] incl. filters and actuator`);
      metric(kc.metrics, 'Blocks', r.blocks.join(' · '));
      const w = r.ctrl_w.map(wconv);
      kc.chart = new LB.Chart(kc.host, { title: 'Controller magnitude', series: [{ name: '|K|', x: w, y: r.ctrl_mag.map(db), fx: fxFreq(r.K_modal, 'db'), color: S(1), unit: 'dB' }], xlog: true, ylabel: 'Magnitude [dB]', xname: 'ω', xunit: wunit(), height: 180 });
      const hp = h('div', null, kc.el);
      const kp2 = new LB.Chart(hp, { title: 'Controller phase', series: [{ name: '∠K', x: w, y: r.ctrl_phase, fx: fxFreq(r.K_modal, 'phase'), color: S(1), unit: 'deg' }], xlog: true, xlabel: wlabel(), ylabel: 'Phase [deg]', xname: 'ω', xunit: wunit(), height: 150 });
      LB.linkX([kc.chart, kp2]);
      // design model vs linearisation
      const W = d.map(x => x.wind);
      const g2 = h('div', 'row2', P);
      const ac = card(g2, 'Plant pole A of the 1-DOF rotor model', 'ω̇ = A·ω + B·β  —  from the linearisation, from the Cp table, from ROSCO tuning');
      const aser = [];
      if (d.some(x => x.A_lin !== undefined)) aser.push({ name: 'Linearisation (rotor-speed mode)', x: W, y: d.map(x => x.A_lin ?? null), color: S(1), marker: 'circle', size: 3 });
      if (d.some(x => x.A_cp !== undefined)) aser.push({ name: 'Cp table at operating point', x: W, y: d.map(x => x.A_cp ?? null), color: S(2), marker: 'square', size: 3 });
      if (d.some(x => x.A_rosco !== undefined)) aser.push({ name: 'ROSCO tuning schedule', x: W, y: d.map(x => x.A_rosco ?? null), color: S(3), dash: 'dash' });
      chartIn(ac, { series: aser, xlabel: 'Wind speed [m/s]', ylabel: 'A [1/s]', xname: 'Wind', xunit: 'm/s', height: 280 });
      const bcc = card(g2, 'Pitch input gain B of the 1-DOF model', '(rad/s of generator speed per s) per rad of pitch');
      const bser = [];
      if (d.some(x => x.B_lin !== undefined)) bser.push({ name: 'Linearisation (rotor-speed mode residue)', x: W, y: d.map(x => x.B_lin ?? null), color: S(1), marker: 'circle', size: 3 });
      if (d.some(x => x.B_cp !== undefined)) bser.push({ name: 'Cp table at operating point', x: W, y: d.map(x => x.B_cp ?? null), color: S(2), marker: 'square', size: 3 });
      if (d.some(x => x.B_rosco !== undefined)) bser.push({ name: 'ROSCO tuning schedule', x: W, y: d.map(x => x.B_rosco ?? null), color: S(3), dash: 'dash' });
      chartIn(bcc, { series: bser, xlabel: 'Wind speed [m/s]', ylabel: 'B_β', xname: 'Wind', xunit: 'm/s', height: 280 });
      const g3 = h('div', 'row2', P);
      const oc = card(g3, 'Speed-loop natural frequency', 'target (YAML) · implied by DISCON gains on the 1-DOF model · rotor-speed pole of the full closed loop');
      const oser = [];
      if (d.some(x => x.omega_target !== undefined)) oser.push({ name: 'Target ω_pc', x: W, y: d.map(x => x.omega_target ?? null), color: S(3), dash: 'dash' });
      oser.push({ name: 'Implied by DISCON gains (1-DOF)', x: W, y: d.map(x => x.omega_implied ?? null), color: S(2), marker: 'square', size: 3 });
      oser.push({ name: 'Full closed loop', x: W, y: d.map(x => x.omega_achieved ?? null), color: S(1), marker: 'circle', size: 3.4 });
      chartIn(oc, { series: oser, xlabel: 'Wind speed [m/s]', ylabel: 'ω_n [rad/s]', xname: 'Wind', xunit: 'm/s', height: 280, yinclude: 0 });
      const zc = card(g3, 'Speed-loop damping ratio', '');
      const zser = [];
      if (d.some(x => x.zeta_target !== undefined)) zser.push({ name: 'Target ζ_pc', x: W, y: d.map(x => x.zeta_target ?? null), color: S(3), dash: 'dash' });
      zser.push({ name: 'Implied by DISCON gains (1-DOF)', x: W, y: d.map(x => x.zeta_implied ?? null), color: S(2), marker: 'square', size: 3 });
      zser.push({ name: 'Full closed loop', x: W, y: d.map(x => x.zeta_achieved ?? null), color: S(1), marker: 'circle', size: 3.4 });
      chartIn(zc, { series: zser, xlabel: 'Wind speed [m/s]', ylabel: 'ζ [–]', xname: 'Wind', xunit: 'm/s', height: 280, yinclude: 0 });
    },
  };

  // ------------------------------------------------------------------ azimuth-file debugging
  /** Rotor speed vs simulation time for one operating point (right of the row), then a card with every
   *  azimuth file and how far each single-azimuth model's modes are from the averaged model. */
  function renderAzimuthDebug(row, o) {
    const oc = o.omega_check || {}, fd = o.file_diag || [], az = o.az_sens;
    const kindTxt = { drift: 'drift in time', periodic: 'periodic in azimuth', scatter: 'no pattern', none: 'constant' }[oc.kind || 'none'];
    const c = card(row, `Rotor speed at ${fmt(o.wind, 3)} m/s, in the order it was linearised`, 'x = simulation time from each file header · labels = azimuth');
    metric(c.metrics, 'Spread', fmt((oc.spread || 0) * 100, 3) + ' %', kindTxt, oc.level === 'warning' ? 'bad' : (oc.level === 'ok' ? 'good' : ''));
    if (oc.slope_rpm_s !== null && oc.slope_rpm_s !== undefined) metric(c.metrics, 'Trend dΩ/dt', fmt(oc.slope_rpm_s, 3), 'rpm/s');
    if (oc.r2_time !== null && oc.r2_time !== undefined) metric(c.metrics, 'Fit: time / azimuth', `${fmt(oc.r2_time, 2)} / ${oc.r2_azimuth === null ? '–' : fmt(oc.r2_azimuth, 2)}`, 'adjusted R²');
    if (oc.message) h('div', 'notes', c.extra, oc.message);
    const byT = fd.slice().sort((a, b) => (a.time ?? 0) - (b.time ?? 0));
    const hasT = byT.every(f => f.time !== null && f.time !== undefined);
    const mean = fd.reduce((s, f) => s + f.rotor_rpm, 0) / Math.max(1, fd.length);
    chartIn(c, { series: [{ name: 'Ω', x: byT.map((f, k) => (hasT ? f.time : k)), y: byT.map(f => f.rotor_rpm), color: oc.level === 'warning' ? cssv('--crit') : S(1), marker: 'circle', size: 3.6, sortedX: false,
      tips: byT.map(f => `<b>${f.file}</b><br>azimuth ${fmt(f.azimuth_deg, 4)}° · t ${fmt(f.time, 5)} s<br>Ω ${fmt(f.rotor_rpm, 6)} rpm (${f.rotor_rpm >= mean ? '+' : ''}${fmt((f.rotor_rpm / mean - 1) * 100, 3)} %)`) }],
      xlabel: hasT ? 'Simulation time [s]' : 'File order', ylabel: 'Rotor speed [rpm]', xname: 't', height: 250, hover: 'nearest', legend: false,
      annotations: byT.map((f, k) => ({ type: 'point', x: hasT ? f.time : k, y: f.rotor_rpm, size: 0.01, label: fmt(f.azimuth_deg, 3) + '°', textColor: cssv('--muted'), dy: -9, fit: false }))
        .concat([{ type: 'hline', y: mean, color: cssv('--muted'), label: 'mean ' + fmt(mean, 5) + ' rpm' }]) });

    const d = card(row.parentNode, `Azimuth files at ${fmt(o.wind, 3)} m/s`, 'per file · Δf, Δζ: single-azimuth model vs averaged', { nochart: true });
    row.parentNode.insertBefore(d.el, row.nextSibling);
    if (az) {
      metric(d.metrics, 'Largest frequency change', fmt(az.df * 100, 3) + ' %', `${az.df_mode} (${fmt(az.df_fn, 3)} Hz)`, az.df > 0.01 ? 'bad' : '');
      metric(d.metrics, 'Largest damping change', fmt(az.dz * 100, 3) + ' points', `${az.dz_mode} (ζ ${fmt(az.dz_zeta * 100, 3)} %)`, az.dz > 0.005 ? 'bad' : '');
    }
    if (az && az.modes && az.modes.length) {
      const mc = card(d.el, 'Mode frequency in each single-azimuth model', 'Δf vs averaged model');
      d.el.appendChild(mc.el);
      const fdAz = fd.map(f => f.azimuth_deg);
      chartIn(mc, { series: az.modes.slice(0, 8).map((m, k) => ({ name: `${m.label} (${fmt(m.fn, 3)} Hz)`, x: fdAz, y: m.df.map(v => v * 100), color: S(k + 1), marker: 'circle', size: 3, unit: '%' })),
        xlabel: 'Azimuth [deg]', ylabel: 'Δf vs averaged [%]', xname: 'ψ', xunit: '°', height: 260, zeroline: true });
    }
    const wrap = h('div', 'tbl-wrap', d.el);
    const t = h('table', 'tbl', wrap);
    t.innerHTML = '<thead><tr><th class="l">File</th><th>Azimuth [deg]</th><th>Time [s]</th><th>Ω [rpm]</th><th>ΔΩ [%]</th><th>Pitch [deg]</th><th>Gen torque [kN·m]</th><th>Gen speed [rpm]</th><th>max |Δf| [%]</th><th>max |Δζ| [pt]</th></tr></thead>';
    const tb = h('tbody', null, t);
    fd.forEach(f => {
      const tr = h('tr', null, tb);
      h('td', 'l', tr, f.file); h('td', 'num', tr, fmt(f.azimuth_deg, 4)); h('td', 'num', tr, fmt(f.time, 5));
      h('td', 'num', tr, fmt(f.rotor_rpm, 6));
      const dv = h('td', 'num', tr, fmt((f.rotor_rpm / mean - 1) * 100, 3)); if (Math.abs(f.rotor_rpm / mean - 1) > 0.0025) dv.style.color = cssv('--crit-ink');
      h('td', 'num', tr, fmt(f.pitch_deg, 4)); h('td', 'num', tr, f.gen_torque === null ? '–' : fmt(f.gen_torque / 1000, 4)); h('td', 'num', tr, fmt(f.gen_rpm, 5));
      h('td', 'num', tr, f.max_df === null ? '–' : fmt(f.max_df * 100, 3)); h('td', 'num', tr, f.max_dz === null ? '–' : fmt(f.max_dz * 100, 3));
    });
  }

  // ------------------------------------------------------------------ gain study helpers
  const ZBINS = [
    [-Infinity, -0.02, '#a3201d', '< −2 % (unstable)'], [-0.02, 0, '#e46962', '−2 … 0 % (unstable)'],
    [0, 0.02, '#f2b64a', '0 … 2 %'], [0.02, 0.05, '#f7dc96', '2 … 5 %'], [0.05, 0.1, '#d6e7fa', '5 … 10 %'],
    [0.1, 0.3, '#9ec5f4', '10 … 30 %'], [0.3, 0.7, '#4f95e8', '30 … 70 %'], [0.7, Infinity, '#1c5cab', '≥ 70 %']];
  function zColor(v, unstable) {
    if (unstable) return (v !== null && v !== undefined && v < -0.02) ? ZBINS[0][2] : ZBINS[1][2];
    if (v === null || v === undefined || !isFinite(v)) return cssv('--grid');
    return (ZBINS.find(b => v >= b[0] && v < b[1]) || ZBINS[ZBINS.length - 1])[2];
  }
  function zLegend(parent, note) {
    const box = h('div', 'zlegend', parent);
    ZBINS.forEach(b => { const s = h('span', null, box); const i = h('i', null, s); i.style.background = b[2]; s.appendChild(document.createTextNode(b[3])); });
    if (note) h('span', 'muted', box, note);
  }
  /** Marching squares on cell centres: level set of Z (rows = y) as a polyline with null breaks. */
  function contour(xs, ys, Z, level, xlog, ylog) {
    const X = [], Y = [];
    const lerp = (a, b, t, lg) => (lg ? Math.pow(10, Math.log10(a) + t * (Math.log10(b) - Math.log10(a))) : a + t * (b - a));
    const ok = (v) => v !== null && v !== undefined && isFinite(v);
    for (let j = 0; j < ys.length - 1; j++) for (let i = 0; i < xs.length - 1; i++) {
      const c = [[xs[i], ys[j], Z[j][i]], [xs[i + 1], ys[j], Z[j][i + 1]], [xs[i + 1], ys[j + 1], Z[j + 1][i + 1]], [xs[i], ys[j + 1], Z[j + 1][i]]];
      if (!c.every(p => ok(p[2]))) continue;
      const pts = [];
      for (let k = 0; k < 4; k++) {
        const a = c[k], b = c[(k + 1) % 4];
        if ((a[2] - level) * (b[2] - level) < 0) {
          const t = (level - a[2]) / (b[2] - a[2]);
          pts.push([lerp(a[0], b[0], t, xlog), lerp(a[1], b[1], t, ylog)]);
        }
      }
      for (let k = 0; k + 1 < pts.length; k += 2) { X.push(pts[k][0], pts[k + 1][0], null); Y.push(pts[k][1], pts[k + 1][1], null); }
    }
    return { x: X, y: Y };
  }

  function shortLab(l) { return (l || '').replace(/^(ED|SrvD|AD|IfW|HD|BD_\d|ExtPtfm|MAP|MD)\s+/, '').replace(/^controller command: /, ''); }
  function famOfLabel(lab) {
    const l = (lab || '').toLowerCase();
    if (l.startsWith('platform')) return 'platform';
    if (l.startsWith('tower')) return 'tower';
    if (l.startsWith('rotor') || l.startsWith('drivetrain')) return 'drivetrain';
    if (l.includes('flap')) return 'blade flap';
    if (l.includes('edge')) return 'blade edge';
    return 'other';
  }
  function phaseRefs(ph) {
    // every −180° + k·360° line near the data, with margin so they stay visible when panning
    const lo = Math.min(...ph) - 1440, hi = Math.max(...ph) + 1440, out = [];
    for (let k = Math.ceil((lo + 180) / 360); k <= Math.floor((hi + 180) / 360); k++) out.push({ type: 'hline', y: -180 + 360 * k, color: cssv('--muted'), dash: 'solid', width: 1 });
    return out;
  }
  function clMetrics(M, r, nyq) {
    const ok = r.stable;
    metric(M, 'Closed loop', ok ? 'stable' : `unstable (${r.Z} RHP)`, '', ok ? 'good' : 'bad');
    if (r.gm_min) metric(M, 'Gain margin', fmt(r.gm_min.gm_db, 3) + ' dB', `@ ${fw(r.gm_min.w)}`, r.gm_min.gm_db < 6 ? 'bad' : '');
    else metric(M, 'Gain margin', '∞');
    if (r.pm_min) metric(M, 'Phase margin', fmt(r.pm_min.pm, 3) + '°', `@ ${fw(r.pm_min.w)}`, r.pm_min.pm < 30 ? 'bad' : '');
    metric(M, 'Delay margin', r.dm === null ? '–' : fmt(r.dm, 3) + ' s');
    metric(M, 'Stability margin s_m', fmt(r.s_m, 3), `@ ${fw(r.w_sm)}`, r.s_m < 0.4 ? 'bad' : '');
    if (ok && r.disk_gm_db !== null && r.disk_gm_db !== undefined) metric(M, 'Disk margin', `±${fmt(r.disk_gm_db, 3)} dB, ±${fmt(r.disk_pm, 3)}°`, 'gain and phase together', r.disk_gm_db < 3 ? 'bad' : '');
    if (nyq) {
      metric(M, 'Open-loop RHP poles P', r.P);
      metric(M, 'Clockwise encirclements N', r.N);
      metric(M, 'Closed-loop RHP poles Z', r.Z, r.nyquist_ok ? 'Z = N + P ✓' : 'Z ≠ N + P', r.nyquist_ok ? '' : 'bad');
    }
    if (r.notes && r.notes.length) { const nb = h('div', 'notes', M.parentNode); r.notes.forEach(n => h('div', null, nb, n)); M.parentNode.insertBefore(nb, M.nextSibling); }
  }
  async function closedData(P) {
    const k = 'cl:' + st.op;
    if (!st.cache[k]) {
      const w = P ? loading(P, 'Closing the loop…') : null;
      try { st.cache[k] = await api('/api/closed?op=' + st.op); } finally { if (w) w.remove(); }
    }
    return st.cache[k];
  }
  function defaultAmp(inp) {
    if (!inp) return 1;
    const u = (inp.disp_unit || '').toLowerCase();
    if (u === 'deg') return 1;
    if (u === 'm/s') return 1;
    if (u.startsWith('n')) return 1e5;
    return 1;
  }

  // ================================================================== ONE-CLICK REPORT
  // Everything the review needs in one self-contained HTML file: inputs, warnings, operating points,
  // Campbell diagram, and (with a controller) stability, margins, gain headroom and loop interaction.
  // Charts are drawn off screen with the same chart code and embedded as SVG.
  const esc = (v) => String(v === null || v === undefined ? '–' : v).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  async function buildReport() {
    const s = st.summary, I = s.info, ops = s.ops, W = ops.map(o => o.wind);
    const stage = h('div', null, document.body);
    stage.style.cssText = 'position:absolute;left:-12000px;top:0;width:920px';
    const fig = (title, opts, note) => {
      const host = h('div', null, stage);
      const c = new LB.Chart(host, Object.assign({ height: 300, legend: true, title }, opts));
      const svg = c.toSVGString();
      host.remove();
      return `<figure>${svg}${note ? `<figcaption>${esc(note)}</figcaption>` : ''}</figure>`;
    };
    const table = (head, rows) => `<table><thead><tr>${head.map(x => `<th>${x}</th>`).join('')}</tr></thead><tbody>${rows.map(r => `<tr>${r.map(x => (x && x.html !== undefined ? `<td class="${x.cls || ''}">${x.html}</td>` : `<td>${esc(x)}</td>`)).join('')}</tr>`).join('')}</tbody></table>`;
    const n = (v, d) => (v === null || v === undefined || !isFinite(v) ? '–' : fmt(v, d || 3));
    const bad = (txt) => ({ html: esc(txt), cls: 'bad' }), good = (txt) => ({ html: esc(txt), cls: 'good' });
    const cl = (s.closed || []).filter(r => !r.error), hasCl = cl.length > 0 && !s.closed_pending;
    const parts = [];
    // ---- verdict
    // informational notes (how files were grouped) are listed but not counted as warnings
    const isNote = (w) => /different name roots|single azimuth|rotor speed varies by .* with azimuth/.test(w);
    const allMsg = ops.flatMap(o => (o.warnings || []).map(w => [w, `${fmt(o.wind, 3)} m/s: ${w}`]));
    const warn = allMsg.filter(x => !isNote(x[0])).map(x => x[1]).concat(I.scan_warnings || []);
    const infoNotes = [...new Set(allMsg.filter(x => isNote(x[0])).map(x => x[0]))];
    const unst = cl.filter(r => !r.stable);
    const olUn = (s.campbell.tracks || []).filter(t => t.points.some(p => p.zeta < 0));
    const verdict = [];
    verdict.push(olUn.length ? bad(`Open loop: ${olUn.length} mode(s) with negative damping (${olUn.map(t => t.label).join(', ')})`) : good('Open loop: every tracked structural mode is damped at all wind speeds'));
    if (hasCl) {
      verdict.push(unst.length ? bad(`Closed loop: UNSTABLE at ${unst.map(r => fmt(r.wind, 3)).join(', ')} m/s`) : good(`Closed loop: stable at all ${cl.length} operating points`));
      const st_ = cl.filter(r => r.stable);
      const mn = (k) => { const v = st_.filter(r => r[k] !== null && r[k] !== undefined); return v.length ? v.reduce((a, b) => (b[k] < a[k] ? b : a)) : null; };
      const g = mn('gm_db'), p = mn('pm'), m = mn('s_m'), d = mn('disk_gm_db');
      if (g) verdict.push({ html: `Worst gain margin <b>${n(g.gm_db)} dB</b> at ${n(g.wind)} m/s`, cls: g.gm_db < 6 ? 'bad' : '' });
      if (p) verdict.push({ html: `Worst phase margin <b>${n(p.pm)}°</b> at ${n(p.wind)} m/s`, cls: p.pm < 30 ? 'bad' : '' });
      if (m) verdict.push({ html: `Worst stability margin s<sub>m</sub> <b>${n(m.s_m)}</b> at ${n(m.wind)} m/s`, cls: m.s_m < 0.4 ? 'bad' : '' });
      if (d) verdict.push({ html: `Worst disk margin <b>±${n(d.disk_gm_db)} dB and ±${n(d.disk_pm)}°</b> together, at ${n(d.wind)} m/s`, cls: d.disk_gm_db < 3 ? 'bad' : '' });
    }
    verdict.push(warn.length ? bad(`${warn.length} warning(s) about the input data (listed below)`) : good('No warnings about the input data'));
    parts.push(`<h2>Summary</h2><ul class="verdict">${verdict.map(v => `<li class="${v.cls || ''}">${v.html}</li>`).join('')}</ul>`);
    // ---- inputs
    parts.push('<h2>Inputs and model</h2>' + table(['Item', 'Value'], [
      ['Linearisation folder', I.lin_dir], ['Files / operating points', `${I.n_files} / ${I.n_ops}`], ['Wind range', `${n(I.wind_range[0])}–${n(I.wind_range[1])} m/s`],
      ['OpenFAST title', I.title], ['Modules', I.modules], ['States / inputs / outputs', `${I.n_states} / ${I.n_inputs} / ${I.n_outputs}`],
      ['Structural DOFs', (I.dofs || []).join(', ')], ['Blade DOFs', I.has_blade_dofs ? 'yes' : 'none (no blade or whirl modes can appear)'],
      ['Removed states', (I.removed_states || []).map(x => x.replace(/\s*\(internal.*$/, '')).join(', ') || 'none'],
      ['DISCON', I.discon || 'not given'], ['Tuning YAML', I.yaml || 'not given'], ['Cp table', I.cp || 'not given'], ['OpenFAST model', I.fst || 'not given']]));
    if (warn.length) parts.push('<h3>Warnings</h3><ul>' + warn.map(w => `<li class="bad">${esc(w)}</li>`).join('') + '</ul>');
    if (infoNotes.length) parts.push('<h3>Notes</h3><ul>' + infoNotes.map(w => `<li>${esc(w)}</li>`).join('') + '</ul>');
    // ---- operating points
    parts.push('<h2>Operating points</h2>');
    parts.push('<div class="two">' + fig('Rotor speed', { series: [{ name: 'Rotor speed', x: W, y: ops.map(o => o.rotor_rpm), color: S(1), marker: 'circle', size: 3 }], xlabel: 'Wind speed [m/s]', ylabel: 'Rotor speed [rpm]', height: 240 })
      + fig('Blade pitch', { series: [{ name: 'Pitch', x: W, y: ops.map(o => o.pitch_deg), color: S(1), marker: 'circle', size: 3 }], xlabel: 'Wind speed [m/s]', ylabel: 'Pitch [deg]', height: 240 }) + '</div>');
    parts.push(table(['Wind [m/s]', 'Rotor [rpm]', 'Ω spread across azimuths', 'Pitch [deg]', 'Torque [MN·m]', 'Power [MW]', 'Azimuths', 'Periodicity raw → MBC', 'Azimuth models Δf / Δζ'],
      ops.map(o => { const oc = o.omega_check || {}, az = o.az_sens; return [n(o.wind, 4), n(o.rotor_rpm, 4),
        oc.spread > 1e-5 ? { html: `${n(oc.spread * 100, 2)} % (${esc(oc.kind)})`, cls: oc.level === 'warning' ? 'bad' : '' } : '0',
        n(o.pitch_deg, 4), n(o.gen_torque === null ? null : o.gen_torque / 1e6, 4), n(o.power_kw === null ? null : o.power_kw / 1000, 4), o.n_az, `${n(o.periodicity_raw * 100, 2)} % → ${n(o.periodicity_mbc * 100, 2)} %`,
        az ? `${n(az.df * 100, 2)} % / ${n(az.dz * 100, 2)} pt` : '–']; })));
    // ---- Campbell
    const camp = (data, title, ghost) => {
      const fmaxD = Math.max(...data.tracks.map(t => Math.max(...t.points.map(p => p.fn))));
      const fmax = Math.min(fmaxD * 1.08, 4);
      const ser = data.tracks.map((t, k) => ({ name: t.label, x: t.points.map(p => p.wind), y: t.points.map(p => p.fn), color: S(k % 8 + 1), dash: SYMDASH[t.sym || ''], marker: SYMMARK[t.sym || ''] || 'circle', size: 3 }));
      const harm = [1, 3, 6].map(m => ({ name: m + 'P', x: data.rotor.map(r => r.wind), y: data.rotor.map(r => r.f1p * m), color: cssv('--baseline'), width: 1.2, dash: 'long', nolegend: true }));
      const gh = (ghost || []).map(t => ({ name: 'open loop', x: t.points.map(p => p.wind), y: t.points.map(p => p.fn), color: cssv('--muted'), width: 1, nolegend: true }));
      return fig(title, { series: harm.concat(gh, ser), xlabel: 'Wind speed [m/s]', ylabel: 'Natural frequency [Hz]', ydomain: [0, fmax], height: 400 }, 'Long dashes: 1P, 3P, 6P.' + (ghost ? ' Grey: open loop.' : ''))
        + fig(title + ': damping', { series: ser.map((x, k) => Object.assign({}, x, { y: data.tracks[k].points.map(p => p.zeta * 100) })), xlabel: 'Wind speed [m/s]', ylabel: 'Damping ratio [%]', height: 280, legend: false,
          annotations: [{ type: 'hline', y: 0, color: cssv('--crit'), dash: 'solid', width: 1 }] });
    };
    const modeTable = (data) => table(['Mode', 'Component', 'f min [Hz]', 'f max [Hz]', 'ζ min [%]', 'at [m/s]'], data.tracks.map(t => {
      const zs = t.points.map(p => p.zeta), iz = zs.indexOf(Math.min(...zs)), fs = t.points.map(p => p.fn);
      return [t.label, t.family, n(Math.min(...fs), 4), n(Math.max(...fs), 4), zs[iz] < 0 ? bad(n(zs[iz] * 100)) : n(zs[iz] * 100), n(t.points[iz].wind)]; }));
    if (s.campbell.tracks.length) parts.push('<h2>Open-loop modes</h2>' + camp(s.campbell, 'Campbell diagram, open loop') + modeTable(s.campbell));
    // ---- closed loop
    if (hasCl) {
      const CW = cl.map(r => r.wind);
      const tau = st.delayMs === '' || st.delayMs === undefined ? 0 : +st.delayMs / 1000;
      let dly = null, sc = null, it = null, ccl = null;
      try { if (tau > 0) dly = (await api('/api/delay?tau=' + tau)).rows; } catch (e) { /* section left out */ }
      try { sc = await api('/api/gain_scaling', {}); } catch (e) { /* section left out */ }
      try { it = await api('/api/interaction'); } catch (e) { /* section left out */ }
      try { ccl = await api('/api/campbell?closed=1'); } catch (e) { /* section left out */ }
      const dOf = (r) => (dly ? dly.find(x => x.op === r.op) : null) || {};
      const mser = (k, name, col) => [{ name, x: CW, y: cl.map(r => r[k]), color: col, marker: 'circle', size: 3, markers: cl.map(r => (r.stable ? 'circle' : 'x')) }]
        .concat(dly ? [{ name: `${name}, ${n(tau * 1000)} ms delay`, x: CW, y: cl.map(r => dOf(r)[k]), color: col, dash: 'dash', marker: 'square', size: 2.6 }] : []);
      parts.push('<h2>Closed-loop stability</h2><p>ROSCO controller rebuilt from DISCON.IN and closed around each linearisation. ✕ marks an unstable point.'
        + (dly ? ` Dashed: with a loop delay of ${n(tau * 1000)} ms.` : '') + '</p>');
      parts.push('<div class="two">' + fig('Gain margin', { series: mser('gm_db', 'Gain margin', S(1)), xlabel: 'Wind speed [m/s]', ylabel: 'Gain margin [dB]', height: 260, annotations: [{ type: 'hline', y: 6, label: '6 dB', color: cssv('--muted') }] })
        + fig('Phase margin', { series: mser('pm', 'Phase margin', S(2)), xlabel: 'Wind speed [m/s]', ylabel: 'Phase margin [deg]', height: 260, annotations: [{ type: 'hline', y: 45, label: '45°', color: cssv('--muted') }] }) + '</div>');
      parts.push(table(['Wind [m/s]', 'Loop', 'Kp', 'Ki', 'GM [dB]', 'PM [deg]'].concat(dly ? ['PM with delay [deg]'] : [], ['Disk GM / PM', 'Delay margin [s]', 's<sub>m</sub>', '‖S‖∞ [dB]', 'Status', 'Least-damped mode']),
        cl.map(r => { const reg = r.region === 'pitch', x = dOf(r); return [n(r.wind), r.region_name, n(reg ? r.Kp : r.VS_Kp, 4), n(reg ? r.Ki : r.VS_Ki, 4),
          r.gm_db !== null && r.gm_db < 6 ? bad(n(r.gm_db)) : n(r.gm_db), r.pm !== null && r.pm < 30 ? bad(n(r.pm)) : n(r.pm)]
          .concat(dly ? [x.stable === false ? bad('unstable') : n(x.pm)] : [], [r.disk_gm_db === null || r.disk_gm_db === undefined ? '–' : `±${n(r.disk_gm_db)} dB / ±${n(r.disk_pm)}°`, n(r.dm), r.s_m < 0.4 ? bad(n(r.s_m)) : n(r.s_m), n(r.Smax_db),
            r.stable ? good('stable') : bad('unstable'), r.min_zeta_label ? `${r.min_zeta_label} (${n(r.min_zeta * 100)} %)` : '–']); })));
      const notes = [...new Set(cl.flatMap(r => r.notes || []))];
      if (notes.length) parts.push('<ul>' + notes.map(x => `<li>${esc(x)}</li>`).join('') + '</ul>');
      if (ccl && ccl.tracks.length) parts.push('<h2>Closed-loop modes</h2>' + camp(ccl, 'Campbell diagram, closed loop', s.campbell.tracks) + modeTable(ccl));
      if (sc && sc.rows.length) {
        parts.push('<h2>Gain headroom</h2><p>Both PI gains multiplied by a common factor k at each wind speed (k = 1 is the DISCON value). The loop is stable for k between the two limits.</p>'
          + table(['Wind [m/s]', 'Loop', 'Kp', 'Ki', 'Stable for k from', 'to', 'Headroom [dB]'], sc.rows.map(r => [n(r.wind), r.region === 'pitch' ? 'pitch PI' : 'torque PI', n(r.kp, 4), n(r.ki, 4)]
            .concat(!r.stable_1 ? [bad('unstable at the DISCON gains'), '', ''] : [r.k_lo === null ? '< 0.05' : n(r.k_lo), r.k_hi === null ? '> 20' : n(r.k_hi), r.k_hi === null ? '> 26' : (r.k_hi < 2 ? bad(n(20 * Math.log10(r.k_hi))) : n(20 * Math.log10(r.k_hi)))]))));
      }
      const ir = it ? it.rows.filter(x => !x.error) : [];
      if (ir.length) {
        const gp = (m) => `${m.gm_db === null ? '∞' : n(m.gm_db)} / ${n(m.pm)}`;
        parts.push('<h2>Pitch–torque interaction</h2><p>Both PI loops closed on generator speed at once (as around rated), against each loop alone. Coupling: change of the gain the pitch loop sees at its crossover when the torque loop is closed.</p>'
          + table(['Wind [m/s]', 'Gen speed / rated', 'Loop in use', 'Pitch alone GM / PM', 'Pitch, both closed GM / PM', 'Torque alone PM', 'Torque, both closed PM', 'Coupling [dB]', 'Both closed'],
            ir.map(x => [n(x.wind), n(x.speed_ratio), x.auto === 'pitch' ? 'pitch (R3)' : 'torque (R2)', gp(x.pitch), gp(x.pitch_both), n(x.torque.pm), n(x.torque_both.pm), n(x.coupling_db), x.both_stable ? good('stable') : bad('unstable')])));
      }
    } else if (s.has_controller && s.closed_pending) parts.push('<h2>Closed loop</h2><p>The closed loop was still being computed when this report was made. Create the report again when the Closed loop tab is available.</p>');
    parts.push('<h2>Scope</h2><p>Linear analysis about each operating point: small perturbations, no saturation, rate limits, set-point smoothing or wind-speed estimator. The controller is modelled in continuous time'
      + ' (a loop delay is included only where stated). Confirm a tuning in time-domain OpenFAST simulations.</p>');
    stage.remove();
    const when = new Date().toISOString().slice(0, 16).replace('T', ' ');
    const css = 'body{font:14px/1.5 system-ui,Segoe UI,sans-serif;color:#1c1c1a;max-width:1000px;margin:24px auto;padding:0 20px}h1{font-size:24px;margin:0}h2{font-size:18px;margin:30px 0 8px;border-top:1px solid #ddd;padding-top:14px}h3{font-size:15px}'
      + '.meta{color:#666;font-size:12.5px;margin:4px 0 0}table{border-collapse:collapse;width:100%;font-size:12.5px;margin:8px 0 16px}th,td{border-bottom:1px solid #e3e3e0;padding:4px 7px;text-align:left;vertical-align:top}th{background:#f5f5f2;font-weight:600}'
      + 'td.bad,li.bad{color:#b42323;font-weight:600}td.good,li.good{color:#006300}figure{margin:10px 0}figure svg{max-width:100%;height:auto;border:1px solid #e3e3e0;border-radius:6px}figcaption{font-size:12px;color:#666}'
      + '.two{display:grid;grid-template-columns:1fr 1fr;gap:12px}.verdict{font-size:14.5px;padding-left:20px}.bar{position:sticky;top:0;background:#fff;padding:8px 0;display:flex;gap:8px;justify-content:flex-end}'
      + 'button{font:inherit;padding:5px 12px;border:1px solid #bbb;border-radius:6px;background:#fff;cursor:pointer}@media print{.bar{display:none}h2{break-after:avoid}figure,table{break-inside:avoid}.two{grid-template-columns:1fr}}';
    const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>LinBench report: ${esc(I.name || '')}</title><style>${css}</style></head><body>`
      + `<div class="bar"><button onclick="window.print()">Print / save as PDF</button></div><h1>LinBench report: ${esc(I.name || I.title || '')}</h1>`
      + `<p class="meta">${esc(when)} UTC · LinBench ${esc(I.version)} · ${I.n_ops} operating points, ${n(I.wind_range[0])}–${n(I.wind_range[1])} m/s</p>${parts.join('\n')}</body></html>`;
    return { html, name: (I.name || 'LinBench').replace(/[^\w.-]+/g, '_') + '_LinBench_report.html' };
  }
  $('#btnReport').onclick = async () => {
    const b = $('#btnReport'), w = window.open('', '_blank');      // opened inside the click, filled when ready
    if (w) w.document.write('<p style="font:14px system-ui;margin:40px">Building the report…</p>');
    b.disabled = true; sbState('busy', 'Building the report…');
    try {
      const r = await buildReport();
      if (w) { w.document.open(); w.document.write(r.html); w.document.close(); }
      LB.download(new Blob([r.html], { type: 'text/html' }), r.name);
      sbState('ready', 'Report ready');
    } catch (e) { console.error(e); if (w) w.close(); sbState('failed', 'Report failed'); alert('Report failed: ' + e.message); }
    b.disabled = false;
  };

  // ================================================================== COMMAND PALETTE (Ctrl+K)
  // Views, wind speeds and actions in one list; typing filters (all words must match), Enter runs.
  let palItems = [], palSel = 0;
  function palCommands() {
    const s = st.summary, out = [], res = s && !$('#results').hidden;
    if (res) {
      const clOk = clAvailable(s);
      const alias = { schedule: 'rotor speed pitch torque power azimuth', campbell: 'frequencies whirl', modes: 'eigenvalues damping participation shapes',
        tf: 'bode frequency response', time: 'step impulse', sweep: 'bode sweep', struct: 'controllability observability hsv hankel',
        summary: 'margins verdict delay disk interaction', loop: 'bode sensitivity margins', nyquist: 'encirclement', poles: 'eigenvalues root locus',
        gains: 'kp ki map headroom', cltime: 'step closed', ctrl: 'notch filter design' };
      ['ol', 'cl'].forEach(t => SUBS[t].forEach(([k, lab]) => out.push({ lab, grp: GROUP[t], key: alias[k], ic: icon(k), off: t === 'cl' && !clOk, run: () => setView(t, k) })));
      s.ops.forEach((o, i) => out.push({ lab: `${fmt(o.wind, 4)} m/s`, grp: `Wind speed · ${fmt(o.rotor_rpm, 4)} rpm`, key: fmt(o.wind, 4) + ' ' + fmt(o.wind, 3),
        ic: '<svg viewBox="0 0 24 24"><path d="M3 9h11a3 3 0 1 0-3-3M3 15h15a3 3 0 1 1-3 3"/></svg>', run: () => gotoOp(i) }));
      out.push({ lab: 'Report', grp: 'Action', ic: '<svg viewBox="0 0 24 24"><path d="M7 3h7l5 5v13H7z"/></svg>', run: () => $('#btnReport').click() });
      out.push({ lab: 'Turbine & model', grp: 'Action', ic: '<svg viewBox="0 0 24 24"><path d="M12 12v9M12 12l-7-5M12 12l8-3"/></svg>', run: () => $('#hdrTurbine').click() });
      if (PG.charts) out.push({ lab: 'Analysis log', grp: 'Action', ic: '<svg viewBox="0 0 24 24"><path d="M5 6h14M5 12h14M5 18h9"/></svg>', run: () => $('#btnLog').click() });
      out.push({ lab: 'New analysis', grp: 'Action', ic: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>', run: showSetup });
    }
    const set = (lab, k, v) => out.push({ lab, grp: 'Setting', ic: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><circle cx="12" cy="12" r="8"/></svg>', run: () => setPref(k, v) });
    set(st.freq === 'hz' ? 'Frequency in rad/s' : 'Frequency in Hz', 'freq', st.freq === 'hz' ? 'rad' : 'hz');
    set(document.documentElement.dataset.theme === 'dark' ? 'Light theme' : 'Dark theme', 'theme', document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');
    set(prefs.legend === 'off' ? 'Legends on' : 'Legends off', 'legend', prefs.legend === 'off' ? 'auto' : 'off');
    set(prefs.crosshair === 'xy' ? 'Simple crosshair' : 'Full crosshair', 'crosshair', prefs.crosshair === 'xy' ? 'x' : 'xy');
    out.push({ lab: 'Settings…', grp: 'Setting', ic: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><circle cx="12" cy="12" r="8"/></svg>', run: openSettings });
    out.push({ lab: 'Keyboard shortcuts', grp: 'Help', ic: '<svg viewBox="0 0 24 24"><path d="M3 7h18v10H3zM7 11h1M11 11h1M15 11h1M8 14h8"/></svg>', run: openShortcuts });
    out.push({ lab: 'Guide and maths reference', grp: 'Help', ic: '<svg viewBox="0 0 24 24"><path d="M5 4h10l4 4v12H5zM9 12h6M9 16h6"/></svg>', run: () => window.open('guide.html', '_blank') });
    return out;
  }
  function openPalette() {
    if (!$('#modal').hidden) return;
    closeMenu();
    $('#palette').hidden = false;
    const inp = $('#palInput'); inp.value = ''; palFilter(); inp.focus();
  }
  function closePalette() { $('#palette').hidden = true; }
  function palFilter() {
    const q = $('#palInput').value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    // every word must match, and each word may match a different part (name, group, keywords)
    palItems = palCommands().filter(c => { const t = (c.lab + ' ' + c.grp + ' ' + (c.key || '')).toLowerCase(); return q.every(w => t.includes(w)); });
    // names that contain what was typed come before matches on the group or keywords
    const full = q.join(' '), score = (c) => { const l = c.lab.toLowerCase(); return l.includes(full) ? 2 : q.every(w => l.includes(w)) ? 1 : 0; };
    if (q.length) palItems.sort((a, b) => score(b) - score(a));
    // a number typed: the wind speeds starting with it first
    if (q.length && /^\d/.test(q[0])) {
      const rank = (c) => (c.grp.startsWith('Wind') ? (c.lab.startsWith(q[0]) ? 2 : 1) : 0);
      palItems.sort((a, b) => rank(b) - rank(a));
    }
    palSel = Math.max(0, palItems.findIndex(c => !c.off));
    palDraw();
  }
  function palDraw() {
    const L = $('#palList'); L.innerHTML = '';
    if (!palItems.length) { h('div', 'pal-empty', L, 'Nothing matches.'); return; }
    palItems.slice(0, 60).forEach((c, i) => {
      const it = h('div', 'pal-it' + (i === palSel ? ' on' : '') + (c.off ? ' off' : ''), L);
      it.innerHTML = c.ic; h('span', null, it, c.lab); h('span', 'g', it, c.grp);
      it.onmousemove = () => { if (palSel !== i) { palSel = i; $$('.pal-it', L).forEach((x, j) => x.classList.toggle('on', j === i)); } };
      it.onclick = () => palRun(i);
    });
    const on = $('.pal-it.on', L); if (on) on.scrollIntoView({ block: 'nearest' });
  }
  function palRun(i) { const c = palItems[i]; if (!c || c.off) return; closePalette(); c.run(); }
  $('#palInput').addEventListener('input', palFilter);
  $('#palInput').addEventListener('keydown', (e) => {
    const n = Math.min(60, palItems.length);
    if (e.key === 'ArrowDown') { palSel = (palSel + 1) % Math.max(1, n); palDraw(); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { palSel = (palSel - 1 + n) % Math.max(1, n); palDraw(); e.preventDefault(); }
    else if (e.key === 'Enter') { palRun(palSel); e.preventDefault(); }
    else if (e.key === 'Escape') { closePalette(); e.preventDefault(); }
  });
  $('#palette').addEventListener('mousedown', (e) => { if (e.target === $('#palette')) closePalette(); });
  $('#btnCmd').onclick = openPalette;

  // ================================================================== STATUS BAR
  function sbState(kind, text, pct) {
    const s = $('#sbState');
    s.classList.toggle('busy', kind === 'busy'); s.classList.toggle('failed', kind === 'failed');
    $('#sbStateText').textContent = text;
    $('#sbBar').hidden = pct === undefined || pct === null;
    if (pct !== undefined && pct !== null) $('#sbBarFill').style.width = Math.max(0, Math.min(100, pct)) + '%';
  }
  function sbResults(s) {
    const p = $('#sbPath');
    p.hidden = !(s && s.info && s.info.lin_dir);
    if (!p.hidden) { p.textContent = s.info.lin_dir; p.onclick = () => api('/api/open_folder', { path: s.info.lin_dir }).catch(() => { }); }
    const v = $('#sbVerdict');
    if (!s) { v.hidden = true; return; }
    const cl = (s.closed || []).filter(r => !r.error), un = cl.filter(r => !r.stable);
    v.hidden = false;
    v.className = 'sb verdict ' + (s.closed_pending ? '' : (cl.length ? (un.length ? 'bad' : 'good') : ''));
    v.textContent = s.closed_pending ? `${s.ops.length} operating points · closed loop computing…`
      : cl.length ? (un.length ? `Closed loop UNSTABLE at ${un.map(r => fmt(r.wind, 3)).join(', ')} m/s` : `Closed loop stable at all ${cl.length} points`)
        : `${s.ops.length} operating points · open loop`;
    if (s.info && s.info.version) $('#sbVer').textContent = 'LinBench ' + s.info.version;
  }
  api('/api/version').then(r => { $('#sbVer').textContent = 'LinBench ' + r.version; }).catch(() => { });

  // keyboard: Ctrl+K palette, Ctrl+, settings, Ctrl+B view list, ? shortcuts; ← / → step through the
  // operating points (not while typing in a field or with a dialog open)
  document.addEventListener('keydown', (e) => {
    const typing = /^(INPUT|SELECT|TEXTAREA)$/.test((e.target && e.target.tagName) || '');
    const ctl = e.ctrlKey || e.metaKey, k = e.key.toLowerCase();
    if (ctl && !e.altKey && k === 'k') { e.preventDefault(); if ($('#palette').hidden) openPalette(); else closePalette(); return; }
    if (ctl && !e.altKey && k === ',') { e.preventDefault(); if (!$('#palette').hidden) closePalette(); if ($('#modal').hidden) openSettings(); return; }
    if (ctl && !e.altKey && k === 'b' && !$('#results').hidden) { e.preventDefault(); $('#btnNav').click(); return; }
    if (e.key === 'Escape' && !$('#menu').hidden) { closeMenu(); return; }
    if (typing || ctl || e.altKey || !$('#modal').hidden || !$('#palette').hidden || !$('#browser').hidden) return;
    if (e.key === '?') { openShortcuts(); e.preventDefault(); return; }
    if ($('#results').hidden) return;
    if (e.key === 'ArrowRight') { gotoOp(st.op + 1); e.preventDefault(); }
    if (e.key === 'ArrowLeft') { gotoOp(st.op - 1); e.preventDefault(); }
  });

  window.LBApp = { showSetup, buildReport };
  showSetup();
})();
