/* LinBench chart library - dependency-free SVG charts.
   Interaction: drag pans the plot in both directions (mouse or touch); Ctrl/⌘ + wheel or a
   trackpad pinch zooms about the cursor.  Shift+drag (or box mode) draws a zoom box: a thin
   horizontal drag zooms x only, a thin vertical drag zooms y only.  Double-click or the reset
   button restores the full view.  Hover shows a crosshair and values; clicking a legend entry
   hides/shows that series.
   Shared with TuneMe (taken back from TuneMe's copy): engineering-plot axes (closed frame, ticks on
   all four sides, boxed legend inside the axes), hover and wheel zoom once per frame, the legend
   rebuilt only when it changes, regular heat maps as one image, tooltips through tipAt(i).
   LinBench additions: double-click detected from two quick clicks (native dblclick is a fallback);
   page-wide plot preferences (LB.setPrefs: line width, grid, legend inside / side / top / off with
   "N curves" folding, crosshair with axis read-outs, --plot-bg for a dark theme); legend entries
   highlight their curve on hover and show it alone on a double-click. */
(function () {
  'use strict';
  const NS = 'http://www.w3.org/2000/svg';
  // theme colours: read once per frame (getComputedStyle per marker made redraws slow)
  let cssCache = {}, cssFrame = -1;
  const css = (n) => {
    const f = Math.floor(performance.now() / 250);
    if (f !== cssFrame) { cssCache = {}; cssFrame = f; }
    if (!(n in cssCache)) cssCache[n] = getComputedStyle(document.documentElement).getPropertyValue(n).trim();
    return cssCache[n];
  };
  const num = (v) => typeof v === 'number' && isFinite(v);

  // Plot preferences of the whole page (the app's Settings): line width, grid, legend style, crosshair.
  //   legend: 'auto' (boxed inside the axes; folded to "N curves" beyond legendMax) | 'inside' | 'side' | 'top' | 'off'
  //   grid: 'on' | 'fine' (minor lines on linear axes too) | 'off';  crosshair: 'x' | 'xy' (with axis read-outs) | 'off'
  const PREFS = { lineScale: 1, grid: 'on', legend: 'auto', legendMax: 6, crosshair: 'xy', heightScale: 1 };
  // text colour that reads on a filled label of colour c (#rgb / #rrggbb; white otherwise)
  function inkOn(c) {
    const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec((c || '').trim());
    if (!m) return '#ffffff';
    const h = m[1].length === 3 ? m[1].replace(/./g, '$&$&') : m[1];
    const [r, g, b] = [0, 2, 4].map(k => parseInt(h.slice(k, k + 2), 16) / 255);
    return 0.299 * r + 0.587 * g + 0.114 * b > 0.62 ? '#111111' : '#ffffff';
  }
  const ALL = new Set();                               // live charts, so a preference change redraws them
  function setPrefs(p) {
    Object.assign(PREFS, p || {});
    cssFrame = -1;                                     // colours may have changed with the theme
    for (const c of [...ALL]) {
      if (!c.host.isConnected) { ALL.delete(c); continue; }
      c._legSig = null; c.render();
    }
  }

  function el(tag, attrs, parent) {
    const e = document.createElementNS(NS, tag);
    if (attrs) for (const k in attrs) if (attrs[k] !== undefined && attrs[k] !== null) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  }
  function h(tag, cls, parent, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    if (parent) parent.appendChild(e);
    return e;
  }

  // ------------------------------------------------------------------ number formatting
  function fmt(v, sig) {
    if (v === null || v === undefined || !isFinite(v)) return '–';
    sig = sig || 4;
    const a = Math.abs(v);
    if (a === 0) return '0';
    if (a >= 1e5 || a < 1e-3) return v.toExponential(Math.max(0, sig - 2)).replace('e+', 'e');
    const d = Math.max(0, sig - 1 - Math.floor(Math.log10(a)));
    return v.toFixed(Math.min(d, 6)).replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');
  }
  function tickFmt(v, step) {
    if (!isFinite(v)) return '';
    const a = Math.abs(v);
    if (a !== 0 && (a >= 1e5 || a < 1e-3)) return v.toExponential(1).replace('e+', 'e');
    const d = step ? Math.max(0, -Math.floor(Math.log10(step) + 1e-9)) : 2;
    return v.toFixed(Math.min(d, 6));
  }
  function niceTicks(min, max, count) {
    if (!(isFinite(min) && isFinite(max))) return [];
    if (min === max) { min -= 1; max += 1; }
    const span = max - min;
    const raw = span / Math.max(2, count);
    const mag = Math.pow(10, Math.floor(Math.log10(raw)));
    const norm = raw / mag;
    const step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
    const out = [];
    for (let v = Math.ceil(min / step - 1e-9) * step; v <= max + step * 1e-9; v += step) out.push(Math.abs(v) < step * 1e-9 ? 0 : v);
    out.step = step;
    return out;
  }
  function logTicks(min, max) {
    const lo = Math.floor(Math.log10(min)), hi = Math.ceil(Math.log10(max));
    const major = [], minor = [];
    if (hi - lo > 12) {                     // many decades: label every few, no minor ticks
      const step = Math.ceil((hi - lo) / 8);
      for (let k = Math.ceil(lo / step) * step; k <= hi; k += step) { const b = Math.pow(10, k); if (b >= min && b <= max) major.push(b); }
      return { major, minor };
    }
    for (let k = lo; k <= hi; k++) {
      const b = Math.pow(10, k);
      if (b >= min * 0.999 && b <= max * 1.001) major.push(b);
      for (let m = 2; m < 10; m++) { const v = m * b; if (v > min && v < max) minor.push(v); }
    }
    if (major.length < 2) {                 // less than a decade: label some minors
      const t = [];
      for (const v of minor) { const m = Math.round(v / Math.pow(10, Math.floor(Math.log10(v)))); if (m === 2 || m === 5) t.push(v); }
      return { major: major.concat(t).sort((a, b) => a - b), minor };
    }
    return { major, minor };
  }
  function logLabel(v) {
    const k = Math.log10(v);
    if (Math.abs(k - Math.round(k)) < 1e-9) {
      const r = Math.round(k);
      if (r >= -3 && r <= 4) return String(+(Math.pow(10, r)).toPrecision(1));
      return '1e' + r;
    }
    return String(+v.toPrecision(2));
  }

  // ------------------------------------------------------------------ markers
  function markerPath(kind, x, y, r) {
    switch (kind) {
      case 'x': return `M${x - r},${y - r}L${x + r},${y + r}M${x - r},${y + r}L${x + r},${y - r}`;
      case 'square': return `M${x - r},${y - r}h${2 * r}v${2 * r}h${-2 * r}Z`;
      case 'diamond': return `M${x},${y - r * 1.3}L${x + r * 1.3},${y}L${x},${y + r * 1.3}L${x - r * 1.3},${y}Z`;
      case 'tri-up': return `M${x},${y - r * 1.3}L${x + r * 1.2},${y + r * 0.9}L${x - r * 1.2},${y + r * 0.9}Z`;
      case 'tri-down': return `M${x},${y + r * 1.3}L${x + r * 1.2},${y - r * 0.9}L${x - r * 1.2},${y - r * 0.9}Z`;
      default: return `M${x - r},${y}a${r},${r} 0 1,0 ${2 * r},0a${r},${r} 0 1,0 ${-2 * r},0`;
    }
  }
  const DASH = { solid: null, dash: '7 4', dot: '1.5 3.5', dashdot: '7 3 1.5 3', long: '12 5' };

  // heatmap cell edges from cell centres (geometric mid-points on a log axis)
  function edges(c, log) {
    const n = c.length, e = new Array(n + 1);
    if (n === 1) { e[0] = log ? c[0] / 1.5 : c[0] - 0.5; e[1] = log ? c[0] * 1.5 : c[0] + 0.5; return e; }
    const mid = (a, b) => (log ? Math.sqrt(a * b) : (a + b) / 2);
    for (let i = 1; i < n; i++) e[i] = mid(c[i - 1], c[i]);
    e[0] = log ? c[0] * c[0] / e[1] : 2 * c[0] - e[1];
    e[n] = log ? c[n - 1] * c[n - 1] / e[n - 1] : 2 * c[n - 1] - e[n - 1];
    return e;
  }
  function hmEdges(s, o) {
    if (!s._xe) { s._xe = edges(s.xs, o.xlog); s._ye = edges(s.ys, o.ylog); }
    return [s._xe, s._ye];
  }
  // A heat map on a regular grid is painted once, one pixel per cell, and shown as an image: panning and
  // zooming then move one element instead of rebuilding a rectangle for every cell.
  function heatImage(s, o) {
    if (s._img !== undefined) return s._img;
    s._img = null;
    const [xe, ye] = hmEdges(s, o), nx = s.xs.length, ny = s.ys.length;
    const regular = (e, log) => {
      const f = log ? Math.log10 : (v) => v, d = (f(e[e.length - 1]) - f(e[0])) / (e.length - 1);
      if (!(d > 0)) return false;
      for (let i = 1; i < e.length; i++) if (Math.abs(f(e[i]) - f(e[i - 1]) - d) > 1e-6 * d) return false;
      return true;
    };
    if (nx * ny < 300 || !regular(xe, o.xlog) || !regular(ye, o.ylog)) return null;
    try {
      const c = document.createElement('canvas');
      c.width = nx; c.height = ny;
      const g = c.getContext('2d');
      for (let iy = 0; iy < ny; iy++) for (let ix = 0; ix < nx; ix++) {
        const col = s.color(s.z[iy][ix], ix, iy);
        if (!col || col === 'transparent') continue;
        g.fillStyle = col; g.fillRect(ix, ny - 1 - iy, 1, 1);
      }
      s._img = c.toDataURL();
    } catch (e) { s._img = null; }
    return s._img;
  }
  function findCell(e, v) {
    if (!(v >= e[0] && v <= e[e.length - 1])) return -1;
    let lo = 0, hi = e.length - 1;
    while (hi - lo > 1) { const m = (lo + hi) >> 1; if (e[m] <= v) lo = m; else hi = m; }
    return lo;
  }

  // ------------------------------------------------------------------ Chart
  class Chart {
    constructor(host, opts) {
      this.host = host;
      this.opts = Object.assign({ height: 300, xlog: false, ylog: false, legend: true, pad: 0.04 }, opts || {});
      // page-wide plot size (Settings): heights given by the views are scaled, unless the caller fixed the height
      if (!this.opts.fixedHeight && PREFS.heightScale !== 1) this.opts.height = Math.round(this.opts.height * PREFS.heightScale);
      this.view = null;
      this.hidden = new Set(this.opts.hiddenInit || []);
      this.linked = [];
      host.classList.add('lb-chart');
      host.innerHTML = '';
      host._lbChart = this;
      this.legendEl = h('div', 'lb-legend', host);
      this.wrap = h('div', 'lb-plot', host);
      this.svg = el('svg', { class: 'lb-svg' }, this.wrap);
      this.tip = h('div', 'lb-tip', this.wrap);
      this.tip.style.display = 'none';
      this._bind();
      // width comes from the observer, so a redraw never forces a synchronous page layout
      this._ro = new ResizeObserver((ents) => {
        const w = Math.round(ents[ents.length - 1].contentRect.width);
        if (w > 0 && w !== this._lastW) { this._w = w; this.render(); }
      });
      this._ro.observe(this.wrap);
      for (const c of [...ALL]) if (!c.host.isConnected) ALL.delete(c);
      ALL.add(this);
      this.render();
    }
    set(opts) { Object.assign(this.opts, opts); this.render(); return this; }
    setData(series) { this.opts.series = series; this.render(); return this; }
    reset() { this.view = null; this.render(); this.linked.forEach(c => { c.view = null; c.render(); }); }

    // ---------------------------------------------------------------- domain
    _visible() { return (this.opts.series || []).filter(s => !this.hidden.has(s.name)); }
    _extent(axis) {
      const log = axis === 'x' ? this.opts.xlog : this.opts.ylog;
      let lo = Infinity, hi = -Infinity;
      const push = (v) => { if (num(v) && (!log || v > 0)) { if (v < lo) lo = v; if (v > hi) hi = v; } };
      for (const s of this._visible()) {
        if (s.noextent) continue;
        if (s.type === 'heatmap') { const [xe, ye] = hmEdges(s, this.opts); const e = axis === 'x' ? xe : ye; push(e[0]); push(e[e.length - 1]); continue; }
        const arr = (axis === 'x' ? s._x0 : s._y0) || s[axis] || [];
        for (let i = 0; i < arr.length; i++) push(arr[i]);
      }
      for (const a of (this.opts.annotations || [])) {
        if (a.type === 'point' && a.fit !== false) push(a[axis]);
        if (a.type === 'circle' && a.fit) { push((axis === 'x' ? a.cx : a.cy) - a.r); push((axis === 'x' ? a.cx : a.cy) + a.r); }
      }
      const fixed = axis === 'x' ? this.opts.xdomain : this.opts.ydomain;
      if (fixed) { if (fixed[0] !== null) lo = fixed[0]; if (fixed[1] !== null) hi = fixed[1]; }
      if (!isFinite(lo)) { lo = log ? 0.1 : 0; hi = log ? 10 : 1; }
      if (lo === hi) { if (log) { lo /= 2; hi *= 2; } else { const d = Math.abs(lo) * 0.1 || 1; lo -= d; hi += d; } }
      if (!log && !fixed) {
        const inc = axis === 'x' ? this.opts.xinclude : this.opts.yinclude;
        if (inc !== undefined) for (const v of [].concat(inc)) { lo = Math.min(lo, v); hi = Math.max(hi, v); }
        const p = (hi - lo) * (axis === 'x' ? (this.opts.xpad || 0) : this.opts.pad);
        lo -= p; hi += p;
      }
      if (log && !fixed) { lo = lo / 1.15; hi = hi * 1.15; }
      return [lo, hi];
    }
    _domains(W, H) {
      let xd = this.view && this.view.x ? this.view.x.slice() : this._extent('x');
      let yd = this.view && this.view.y ? this.view.y.slice() : this._extent('y');
      if (this.opts.equal && !this.opts.xlog && !this.opts.ylog) {
        const ux = (xd[1] - xd[0]) / W, uy = (yd[1] - yd[0]) / H;
        if (ux > uy) { const c = (yd[0] + yd[1]) / 2, half = ux * H / 2; yd = [c - half, c + half]; }
        else { const c = (xd[0] + xd[1]) / 2, half = uy * W / 2; xd = [c - half, c + half]; }
      }
      return [xd, yd];
    }

    // ---------------------------------------------------------------- render
    render() {
      const o = this.opts, svg = this.svg;
      if (!this._w) this._w = this.wrap.clientWidth;
      const W0 = Math.max(260, this._w || 600);
      this._lastW = W0;
      const H0 = o.height;
      svg.setAttribute('width', W0); svg.setAttribute('height', H0);
      svg.setAttribute('viewBox', `0 0 ${W0} ${H0}`);
      while (svg.firstChild) svg.removeChild(svg.firstChild);
      const m = { l: o.ml || 62, r: o.mr || 14, t: 10, b: o.xlabel ? 44 : 26 };
      const W = W0 - m.l - m.r, H = H0 - m.t - m.b;
      this.m = m; this.W = W; this.H = H;
      // function series: the full view is the range of the original data, with y taken from the
      // function itself; afterwards the curve is re-sampled over the visible x range so it never
      // ends at the edge of the data it was first drawn with (fewer points while dragging)
      for (const s of (o.series || [])) {
        if (!s.fx || s._x0) continue;
        s._x0 = s.x; s._y0 = s.y;
        const xs = (s.x || []).filter(v => num(v) && (!o.xlog || v > 0));
        if (xs.length) try { const r = s.fx([Math.min(...xs), Math.max(...xs)], 1600); s._x0 = r.x; s._y0 = r.y; } catch (e) { console.error(e); }
      }
      const [xd, yd] = this._domains(W, H);
      this.xd = xd; this.yd = yd;
      for (const s of this._visible()) {
        if (!s.fx) continue;
        try { const r = s.fx(xd, this._dragging ? 450 : 1600); s.x = r.x; s.y = r.y; } catch (e) { console.error(e); }
      }
      const sx = o.xlog ? (v) => m.l + (Math.log10(v) - Math.log10(xd[0])) / (Math.log10(xd[1]) - Math.log10(xd[0])) * W
                        : (v) => m.l + (v - xd[0]) / (xd[1] - xd[0]) * W;
      const sy = o.ylog ? (v) => m.t + H - (Math.log10(v) - Math.log10(yd[0])) / (Math.log10(yd[1]) - Math.log10(yd[0])) * H
                        : (v) => m.t + H - (v - yd[0]) / (yd[1] - yd[0]) * H;
      this.sx = sx; this.sy = sy;
      this.ix = o.xlog ? (px) => Math.pow(10, Math.log10(xd[0]) + (px - m.l) / W * (Math.log10(xd[1]) - Math.log10(xd[0])))
                       : (px) => xd[0] + (px - m.l) / W * (xd[1] - xd[0]);
      this.iy = o.ylog ? (py) => Math.pow(10, Math.log10(yd[0]) + (m.t + H - py) / H * (Math.log10(yd[1]) - Math.log10(yd[0])))
                       : (py) => yd[0] + (m.t + H - py) / H * (yd[1] - yd[0]);
      // The axes are drawn the way engineering plots are read: a white plotting area in a closed dark frame, tick
      // marks pointing inwards on all four sides, a light grid behind the data, dark tick and axis labels.
      const grid = css('--plot-grid') || '#e4e4e4', base = css('--baseline') || '#c3c2b7', muted = css('--muted') || '#898781';
      const ink2 = css('--ink2') || '#52514e', frame = css('--plot-frame') || '#3c3c3c', lab = css('--plot-ink') || '#262626';
      el('rect', { x: 0, y: 0, width: W0, height: H0, fill: css('--card') || '#ffffff' }, svg);
      el('rect', { x: m.l, y: m.t, width: W, height: H, fill: css('--plot-bg') || '#ffffff' }, svg);
      const gAx = el('g', { class: 'lb-axes' }, svg);
      const gTk = el('g', { class: 'lb-ticks', stroke: frame, 'stroke-width': 1 });   // on top of the grid, added after it
      // ---- ticks & grid
      const fs = 11.5, TK = 5, gridOn = PREFS.grid !== 'off';
      const drawX = (v, major, label) => {
        const x = sx(v);
        if (x < m.l - 0.5 || x > m.l + W + 0.5) return;
        if (gridOn) el('line', { x1: x, x2: x, y1: m.t, y2: m.t + H, stroke: grid, 'stroke-width': major ? 1 : 0.6, 'stroke-opacity': major ? 1 : 0.6 }, gAx);
        const t2 = major ? TK : TK * 0.55;
        el('line', { x1: x, x2: x, y1: m.t + H, y2: m.t + H - t2 }, gTk); el('line', { x1: x, x2: x, y1: m.t, y2: m.t + t2 }, gTk);
        if (label !== null) { const t = el('text', { x, y: m.t + H + 16, 'text-anchor': 'middle', fill: lab, 'font-size': fs, class: 'tnum' }, gAx); t.textContent = label; }
      };
      const drawY = (v, major, label) => {
        const y = sy(v);
        if (y < m.t - 0.5 || y > m.t + H + 0.5) return;
        if (gridOn) el('line', { x1: m.l, x2: m.l + W, y1: y, y2: y, stroke: grid, 'stroke-width': major ? 1 : 0.6, 'stroke-opacity': major ? 1 : 0.6 }, gAx);
        const t2 = major ? TK : TK * 0.55;
        el('line', { x1: m.l, x2: m.l + t2, y1: y, y2: y }, gTk); el('line', { x1: m.l + W, x2: m.l + W - t2, y1: y, y2: y }, gTk);
        if (label !== null) { const t = el('text', { x: m.l - 8, y: y + 4, 'text-anchor': 'end', fill: lab, 'font-size': fs, class: 'tnum' }, gAx); t.textContent = label; }
      };
      // fine grid: minor lines between the labelled ones on linear axes as well
      const minorLin = (t, d, draw) => {
        if (PREFS.grid !== 'fine' || !t.step) return;
        const st = t.step / (Math.round(t.step / Math.pow(10, Math.floor(Math.log10(t.step)))) === 2 ? 4 : 5);
        for (let v = Math.ceil(d[0] / st) * st; v <= d[1]; v += st) if (Math.abs(v / t.step - Math.round(v / t.step)) > 1e-6) draw(v, false, null);
      };
      if (o.xlog) { const t = logTicks(xd[0], xd[1]); t.minor.forEach(v => drawX(v, false, null)); t.major.forEach(v => drawX(v, true, logLabel(v))); }
      else { const t = niceTicks(xd[0], xd[1], Math.max(3, Math.floor(W / 80))); minorLin(t, xd, drawX); t.forEach(v => drawX(v, true, o.xtick ? o.xtick(v) : tickFmt(v, t.step))); }
      if (o.ylog) { const t = logTicks(yd[0], yd[1]); t.minor.forEach(v => drawY(v, false, null)); t.major.forEach(v => drawY(v, true, logLabel(v))); }
      else { const t = niceTicks(yd[0], yd[1], Math.max(3, Math.floor(H / 48))); minorLin(t, yd, drawY); t.forEach(v => drawY(v, true, o.ytick ? o.ytick(v) : tickFmt(v, t.step))); }
      // zero lines
      if (!o.ylog && yd[0] < 0 && yd[1] > 0 && o.zeroline !== false) el('line', { x1: m.l, x2: m.l + W, y1: sy(0), y2: sy(0), stroke: base, 'stroke-width': 1.2 }, gAx);
      if (!o.xlog && xd[0] < 0 && xd[1] > 0 && o.xzeroline) el('line', { x1: sx(0), x2: sx(0), y1: m.t, y2: m.t + H, stroke: base, 'stroke-width': 1.2 }, gAx);
      if (o.xlabel) { const t = el('text', { x: m.l + W / 2, y: H0 - 8, 'text-anchor': 'middle', fill: lab, 'font-size': 12.5 }, gAx); t.textContent = o.xlabel; }
      if (o.ylabel) { const t = el('text', { x: 14, y: m.t + H / 2, 'text-anchor': 'middle', fill: lab, 'font-size': 12.5, transform: `rotate(-90 14 ${m.t + H / 2})` }, gAx); t.textContent = o.ylabel; }
      // ---- clip + data
      const cid = 'clip' + Math.random().toString(36).slice(2);
      const defs = el('defs', null, svg);
      const cp = el('clipPath', { id: cid }, defs);
      el('rect', { x: m.l, y: m.t, width: W, height: H }, cp);
      const gD = el('g', { 'clip-path': `url(#${cid})` }, svg);
      for (const s of this._visible()) {
        if (s.type !== 'heatmap') continue;
        const [xe, ye] = hmEdges(s, o), img = heatImage(s, o);
        if (img) {
          const x0 = sx(xe[0]), x1 = sx(xe[s.xs.length]), y0 = sy(ye[s.ys.length]), y1 = sy(ye[0]);
          el('image', { href: img, x: x0, y: y0, width: x1 - x0, height: y1 - y0, preserveAspectRatio: 'none', style: 'image-rendering: pixelated' }, gD);
          continue;
        }
        for (let iy = 0; iy < s.ys.length; iy++) {
          const y0 = sy(ye[iy]), y1 = sy(ye[iy + 1]);
          if (Math.max(y0, y1) < m.t || Math.min(y0, y1) > m.t + H) continue;
          for (let ix = 0; ix < s.xs.length; ix++) {
            const x0 = sx(xe[ix]), x1 = sx(xe[ix + 1]);
            if (Math.max(x0, x1) < m.l || Math.min(x0, x1) > m.l + W) continue;
            el('rect', { x: Math.min(x0, x1), y: Math.min(y0, y1), width: Math.abs(x1 - x0) + 0.6, height: Math.abs(y1 - y0) + 0.6,
              fill: s.color(s.z[iy][ix], ix, iy), 'shape-rendering': 'crispEdges' }, gD);
          }
        }
      }
      this._drawAnnotations(gD, 'below');
      const hiName = this._hi;                       // a legend entry under the pointer: the other curves fade
      for (const s of this._visible()) {
        if (s.type === 'heatmap') continue;
        const color = s.color || css('--s1');
        const xs = s.x || [], ys = s.y || [];
        const op = (s.opacity || 1) * (hiName && s.name !== hiName ? 0.15 : 1), lw = (s.width || 2) * PREFS.lineScale * (hiName && s.name === hiName ? 1.4 : 1);
        if (s.type !== 'scatter') {
          let d = '', pen = false;
          for (let i = 0; i < xs.length; i++) {
            const xv = xs[i], yv = ys[i];
            const ok = num(xv) && num(yv) && (!o.xlog || xv > 0) && (!o.ylog || yv > 0);
            if (!ok) { pen = false; continue; }
            const X = sx(xv), Y = sy(yv);
            if (!isFinite(X) || !isFinite(Y) || Math.abs(Y) > 1e6 || Math.abs(X) > 1e6) { pen = false; continue; }
            d += (pen ? 'L' : 'M') + X.toFixed(1) + ',' + Y.toFixed(1);
            pen = true;
          }
          if (d) el('path', { d, fill: 'none', stroke: color, 'stroke-width': lw, 'stroke-dasharray': DASH[s.dash] || null,
            'stroke-linejoin': 'round', 'stroke-linecap': 'round', opacity: op }, gD);
          if (s.arrows) this._arrows(gD, s, color);
        }
        if (s.type === 'scatter' || s.marker) {
          const r = s.size || 4;
          for (let i = 0; i < xs.length; i++) {
            if (!num(xs[i]) || !num(ys[i])) continue;
            if ((o.xlog && xs[i] <= 0) || (o.ylog && ys[i] <= 0)) continue;
            const X = sx(xs[i]), Y = sy(ys[i]);
            const mk = (s.markers && s.markers[i]) || s.marker || 'circle';
            const hollow = s.hollow || (s.hollowAt && s.hollowAt[i]);
            const col = (s.colors && s.colors[i]) || color;
            el('path', { d: markerPath(mk, X, Y, (s.sizes && s.sizes[i]) || r), fill: mk === 'x' ? 'none' : (hollow ? (css('--surface') || '#fff') : col),
              stroke: mk === 'x' ? col : (hollow ? col : (css('--plot-bg') || '#fff')), 'stroke-width': mk === 'x' ? 2 : (hollow ? 1.6 : 1.2), opacity: op }, gD);
          }
        }
      }
      this._drawAnnotations(gD, 'above');
      // the frame and its tick marks close the axes over the data, so a curve never paints over them
      svg.appendChild(gTk);
      el('rect', { x: m.l, y: m.t, width: W, height: H, fill: 'none', stroke: frame, 'stroke-width': 1, 'shape-rendering': 'crispEdges' }, svg);
      this._drawAnnotations(svg, 'labels');
      // ---- legend
      this._legend();
      this._placeLegend();
      // overlay for interaction
      this.overlay = el('rect', { x: m.l, y: m.t, width: W, height: H, fill: 'transparent', class: 'lb-overlay' }, svg);
      this.cross = el('line', { x1: 0, x2: 0, y1: m.t, y2: m.t + H, stroke: muted, 'stroke-width': 1, 'stroke-dasharray': '3 3', visibility: 'hidden' }, svg);
      this.hl = el('g', { 'pointer-events': 'none' }, svg);
      this.box = el('rect', { class: 'lb-zoombox', visibility: 'hidden' }, svg);
    }
    _arrows(g, s, color) {
      const xs = s.x, ys = s.y, n = xs.length;
      const idx = s.arrows.at || [];
      for (const i of idx) {
        if (i <= 0 || i >= n - 1) continue;
        const X = this.sx(xs[i]), Y = this.sy(ys[i]);
        let j = i + 1;
        while (j < n - 1 && Math.hypot(this.sx(xs[j]) - X, this.sy(ys[j]) - Y) < 3) j++;
        const X2 = this.sx(xs[j]), Y2 = this.sy(ys[j]);
        if (![X, Y, X2, Y2].every(isFinite)) continue;
        const a = Math.atan2(Y2 - Y, X2 - X), L = 9, w = 5;
        const p = `M${X + L * Math.cos(a)},${Y + L * Math.sin(a)}L${X - w * Math.sin(a)},${Y + w * Math.cos(a)}L${X + w * Math.sin(a)},${Y - w * Math.cos(a)}Z`;
        el('path', { d: p, fill: color, stroke: css('--surface') || '#fff', 'stroke-width': 0.8 }, g);
      }
    }
    _drawAnnotations(g, layer) {
      const o = this.opts, m = this.m, W = this.W, H = this.H;
      const ink2 = css('--ink2') || '#52514e', muted = css('--muted');
      for (const a of (o.annotations || [])) {
        const al = a.layer || (a.type === 'point' ? 'above' : 'below');
        if (a.type === 'vline' && layer === 'below') {
          if (o.xlog && a.x <= 0) continue;
          const x = this.sx(a.x);
          el('line', { x1: x, x2: x, y1: m.t, y2: m.t + H, stroke: a.color || muted, 'stroke-width': a.width || 1.2, 'stroke-dasharray': DASH[a.dash || 'dash'] }, g);
        }
        if (a.type === 'vline' && layer === 'labels' && a.label) {
          const x = this.sx(a.x);
          if (x < m.l || x > m.l + W) continue;
          const t = el('text', { x: x + 4, y: m.t + 12 + (a.dy || 0), fill: a.color || ink2, 'font-size': 10.5 }, g); t.textContent = a.label;
        }
        if (a.type === 'hline' && layer === 'below') {
          if (o.ylog && a.y <= 0) continue;
          const y = this.sy(a.y);
          el('line', { x1: m.l, x2: m.l + W, y1: y, y2: y, stroke: a.color || muted, 'stroke-width': a.width || 1.2, 'stroke-dasharray': DASH[a.dash || 'dash'] }, g);
        }
        if (a.type === 'hline' && layer === 'labels' && a.label) {
          const y = this.sy(a.y);
          if (y < m.t || y > m.t + H) continue;
          const t = el('text', { x: m.l + W - 4, y: y - 4, 'text-anchor': 'end', fill: a.color || ink2, 'font-size': 10.5 }, g); t.textContent = a.label;
        }
        if (a.type === 'band' && layer === 'below') {
          const x1 = this.sx(a.x0), x2 = this.sx(a.x1);
          el('rect', { x: Math.min(x1, x2), y: m.t, width: Math.abs(x2 - x1), height: H, fill: a.color || muted, opacity: a.opacity || 0.08 }, g);
        }
        if (a.type === 'circle' && layer === 'below') {
          const cx = this.sx(a.cx), cy = this.sy(a.cy);
          const rx = Math.abs(this.sx(a.cx + a.r) - cx), ry = Math.abs(this.sy(a.cy + a.r) - cy);
          el('ellipse', { cx, cy, rx, ry, fill: a.fill || 'none', 'fill-opacity': a.fillOpacity || 0.08, stroke: a.color || muted,
            'stroke-width': a.width || 1.2, 'stroke-dasharray': DASH[a.dash] || null }, g);
        }
        if (a.type === 'point' && layer === al) {
          if ((o.xlog && a.x <= 0) || (o.ylog && a.y <= 0)) continue;
          const X = this.sx(a.x), Y = this.sy(a.y);
          el('path', { d: markerPath(a.marker || 'circle', X, Y, a.size || 5), fill: a.hollow ? (css('--surface')) : (a.color || ink2),
            stroke: a.hollow ? (a.color || ink2) : (css('--surface')), 'stroke-width': a.marker === 'x' ? 2.2 : 1.5 }, g);
        }
        if (a.type === 'point' && layer === 'labels' && a.label) {
          const X = this.sx(a.x), Y = this.sy(a.y);
          if (X < m.l || X > m.l + W || Y < m.t || Y > m.t + H) continue;
          const anchor = a.anchor || (X > m.l + W * 0.75 ? 'end' : 'start');
          const t = el('text', { x: X + (anchor === 'end' ? -8 : 8), y: Y + (a.dy !== undefined ? a.dy : -8), fill: a.textColor || ink2,
            'font-size': 11, 'text-anchor': anchor, 'font-weight': 600, class: 'lb-halo' }, g); t.textContent = a.label;
        }
      }
    }
    _legend() {
      const o = this.opts, L = this.legendEl;
      const ser = (o.series || []).filter(s => !s.nolegend && s.type !== 'heatmap');
      // rebuilt only when what it shows has changed: a pan or zoom redraw leaves it (and the page layout) alone
      const cb0 = o.colorbar;
      const sig = JSON.stringify([o.legend, PREFS.legend, PREFS.legendMax, !!this._legOpen, cb0 ? [cb0.label, cb0.min, cb0.max, cb0.colors] : 0,
        ser.map(s => [s.name, s.color, s.dash, s.width, s.type, s.marker, s.hollow]), [...this.hidden]]);
      if (sig === this._legSig) return;
      this._legSig = sig;
      L.innerHTML = '';
      L.style.maxHeight = '';
      this.host.classList.remove('lb-side');
      if (o.colorbar) {
        const cb = o.colorbar;
        const row = h('div', 'lb-cbar', L);
        h('span', 'lb-cbar-lab', row, cb.label + ' ');
        h('span', 'lb-cbar-v tnum', row, fmt(cb.min, 3));
        const g = h('span', 'lb-cbar-grad', row);
        g.style.background = `linear-gradient(90deg, ${cb.colors.join(',')})`;
        h('span', 'lb-cbar-v tnum', row, fmt(cb.max, 3));
      }
      L.classList.remove('inside', 'side', 'folded');
      if (!o.legend || PREFS.legend === 'off' || ser.length < 2 || o.colorbar) { L.style.display = o.colorbar ? '' : 'none'; L.style.left = L.style.top = ''; return; }
      L.style.display = '';
      const names = [...new Set(ser.map(s => s.name))];
      // where: the page setting, unless this plot asks for its legend above the axes
      const mode = o.legend === 'top' || PREFS.legend === 'top' ? 'top' : PREFS.legend === 'side' ? 'side' : 'inside';
      if (mode === 'inside') L.classList.add('inside');
      if (mode === 'side') { L.classList.add('side'); this.host.classList.add('lb-side'); L.style.maxHeight = o.height + 'px'; }
      if (mode !== 'inside') L.style.left = L.style.top = '';
      const toggle = (n) => { this._hi = null; if (this.hidden.has(n)) this.hidden.delete(n); else this.hidden.add(n); this.render(); if (o.onToggle) o.onToggle(this.hidden); };
      const setHidden = (set) => { this._hi = null; this.hidden = set; this.render(); if (o.onToggle) o.onToggle(this.hidden); };
      // many curves: a small "N curves" box that opens into a scrolling list (auto mode), with show all / none
      const many = names.length > PREFS.legendMax;
      if (many && (mode !== 'top')) {
        const hd = h('div', 'lb-leg-h', L);
        const nShown = names.filter(n => !this.hidden.has(n)).length;
        const fold = h('button', 'lb-leg-fold', hd, (PREFS.legend === 'auto' && !this._legOpen ? '▸ ' : '▾ ') + `${nShown}/${names.length} curves`);
        fold.type = 'button'; fold.title = 'Show or fold the list of curves';
        fold.onclick = () => { this._legOpen = !this._legOpen; this._legSig = null; this._legend(); this._placeLegend(); };
        if (PREFS.legend !== 'auto' || this._legOpen) {
          const a = h('button', 'lb-leg-all', hd, 'all'); a.type = 'button'; a.title = 'Show every curve'; a.onclick = () => setHidden(new Set());
          const z = h('button', 'lb-leg-all', hd, 'none'); z.type = 'button'; z.title = 'Hide every curve'; z.onclick = () => setHidden(new Set(names));
        }
        if (PREFS.legend === 'auto' && !this._legOpen) { L.classList.add('folded'); return; }
      }
      const list = many ? h('div', 'lb-leg-list', L) : L;
      if (many && mode === 'inside') list.style.maxHeight = Math.max(80, (this.H || o.height - 60) - 50) + 'px';
      const seen = new Set();
      for (const s of ser) {
        if (seen.has(s.name)) continue;
        seen.add(s.name);
        const it = h('button', 'lb-leg' + (this.hidden.has(s.name) ? ' off' : ''), list);
        it.type = 'button';
        it.title = s.name + '\nClick: hide / show · double-click: only this curve (again: all) · hover: highlight';
        const sw = el('svg', { width: 26, height: 12 }, it);
        if (s.type !== 'scatter') el('line', { x1: 1, x2: 25, y1: 6, y2: 6, stroke: s.color, 'stroke-width': Math.min(3, s.width || 2), 'stroke-dasharray': DASH[s.dash] || null }, sw);
        if (s.type === 'scatter' || s.marker) {
          const mk = s.marker || 'circle';
          el('path', { d: markerPath(mk, 13, 6, 4), fill: (mk === 'x' || s.hollow) ? 'none' : s.color, stroke: s.color, 'stroke-width': mk === 'x' ? 2 : 1.4 }, sw);
        }
        h('span', null, it, s.name);
        // click: hide / show.  Two quick clicks: this curve alone, or every curve if it was alone already
        // (detected here: the legend is rebuilt after the first click, so the native dblclick never arrives)
        it.onclick = () => {
          const now = performance.now(), lc = this._legClick;
          if (lc && lc.n === s.name && now - lc.t < 420) {
            this._legClick = null;
            const solo = names.every(n => (n === s.name ? !lc.before.has(n) : lc.before.has(n)));
            setHidden(solo ? new Set() : new Set(names.filter(n => n !== s.name)));
            return;
          }
          this._legClick = { n: s.name, t: now, before: new Set(this.hidden) };
          toggle(s.name);
        };
        it.onmouseenter = () => { if (this.hidden.has(s.name)) return; this._hi = s.name; this.render(); };
        it.onmouseleave = () => { if (this._hi) { this._hi = null; this.render(); } };
      }
    }

    /** Puts the legend into the corner of the axes where it covers the least of the curves (the corner named by
     *  opts.legendPos - 'ne', 'nw', 'se', 'sw' - if one is given). It stays where it is while the plot is dragged. */
    _placeLegend() {
      const L = this.legendEl, o = this.opts;
      if (!L.classList.contains('inside') || L.style.display === 'none') return;
      const m = this.m, W = this.W, H = this.H, top0 = this.wrap.offsetTop, pad = 7;
      const lw = L.offsetWidth, lh = L.offsetHeight;
      if (!lw || !lh) return;
      const corners = { ne: [m.l + W - lw - pad, m.t + pad], nw: [m.l + pad, m.t + pad], se: [m.l + W - lw - pad, m.t + H - lh - pad], sw: [m.l + pad, m.t + H - lh - pad] };
      let pos = o.legendPos && corners[o.legendPos] ? o.legendPos : this._legPos;
      if (!(o.legendPos && corners[o.legendPos]) && !this._dragging) {
        const hits = { ne: 0, nw: 0, se: 0, sw: 0 }, g = 4;   // curves are walked in steps of a few pixels
        const mark = (X, Y) => { for (const k in corners) { const c = corners[k]; if (X >= c[0] - g && X <= c[0] + lw + g && Y >= c[1] - g && Y <= c[1] + lh + g) hits[k]++; } };
        for (const s of this._visible()) {
          if (s.type === 'heatmap') continue;
          const xs = s.x || [], ys = s.y || [];
          let px = null, py = null;
          for (let i = 0; i < xs.length; i++) {
            if (!num(xs[i]) || !num(ys[i]) || (o.xlog && xs[i] <= 0) || (o.ylog && ys[i] <= 0)) { px = null; continue; }
            const X = this.sx(xs[i]), Y = this.sy(ys[i]);
            if (!isFinite(X) || !isFinite(Y)) { px = null; continue; }
            if (px !== null && s.type !== 'scatter') { const n = Math.min(400, Math.ceil(Math.hypot(X - px, Y - py) / 6)); for (let k = 1; k < n; k++) mark(px + (X - px) * k / n, py + (Y - py) * k / n); }
            mark(X, Y); px = X; py = Y;
          }
        }
        pos = ['ne', 'nw', 'se', 'sw'].reduce((best, k) => (hits[k] < hits[best] ? k : best), 'ne');
        this._legPos = pos;
      }
      const c = corners[pos || 'ne'];
      L.style.left = Math.max(m.l + 2, c[0]) + 'px'; L.style.top = (top0 + Math.max(m.t + 2, c[1])) + 'px';
    }

    // ---------------------------------------------------------------- interaction
    // Shift a domain by a fraction of its span (in log10 space on a log axis).
    static _shift(d, frac, log) {
      if (log) { const a = Math.log10(d[0]), b = Math.log10(d[1]), s = (b - a) * frac; return [Math.pow(10, a + s), Math.pow(10, b + s)]; }
      const s = (d[1] - d[0]) * frac; return [d[0] + s, d[1] + s];
    }
    // Scale a domain by f about the value at fraction c (0..1) of the axis.
    static _scale(d, f, c, log) {
      if (log) { const a = Math.log10(d[0]), b = Math.log10(d[1]), m = a + (b - a) * c; return [Math.pow(10, m + (a - m) * f), Math.pow(10, m + (b - m) * f)]; }
      const m = d[0] + (d[1] - d[0]) * c; return [m + (d[0] - m) * f, m + (d[1] - m) * f];
    }
    setDragMode(mode) { this.dragMode = mode; this.svg.classList.toggle('box-mode', mode === 'box'); }
    _setView(v) {
      // keep log axes within 1e-15..1e15 and every axis finite with a non-zero span
      const lim = (d, log, cur) => {
        if (!d) return d;
        let [a, b] = d;
        if (log) { a = Math.max(a, 1e-15); b = Math.min(b, 1e15); return b > a * 1.001 ? [a, b] : cur; }
        return isFinite(a) && isFinite(b) && b - a > 1e-12 * Math.max(1, Math.abs(a)) && b - a < 1e15 ? [a, b] : cur;
      };
      v = { x: lim(v.x, this.opts.xlog, this.xd), y: lim(v.y, this.opts.ylog, this.yd) };
      this.view = v;
      this.render();
      for (const c of this.linked) { c.view = Object.assign({}, c.view || {}, { x: v.x }); c.render(); }
    }
    /** Zoom by factor f (< 1 zooms in) about a point given as axis fractions (default: centre). */
    zoomBy(f, cx, cy) {
      cx = cx === undefined ? 0.5 : cx; cy = cy === undefined ? 0.5 : cy;
      this._setView({ x: Chart._scale(this.xd, f, cx, this.opts.xlog), y: Chart._scale(this.yd, f, cy, this.opts.ylog) });
    }
    _bind() {
      let drag = null, raf = 0, hov = null, hraf = 0, wz = null, wraf = 0;
      this.setDragMode(this.opts.dragMode || 'pan');
      if (this.opts.onClick) this.svg.classList.add('clickable');
      // pointer position in plot units (the page may be zoomed by the text-size setting: rect is zoomed, the svg is not)
      const pos = (ev) => {
        const r = this.svg.getBoundingClientRect(), k = r.width ? (+this.svg.getAttribute('width') || r.width) / r.width : 1;
        return [(ev.clientX - r.left) * k, (ev.clientY - r.top) * k];
      };
      const inside = (p) => p[0] >= this.m.l && p[0] <= this.m.l + this.W && p[1] >= this.m.t && p[1] <= this.m.t + this.H;
      const clamp = (p) => [Math.max(this.m.l, Math.min(this.m.l + this.W, p[0])), Math.max(this.m.t, Math.min(this.m.t + this.H, p[1]))];
      const hideHover = () => { this.tip.style.display = 'none'; if (this.cross) this.cross.setAttribute('visibility', 'hidden'); if (this.hl) this.hl.innerHTML = ''; this._syncPeers(null); };
      // drag = pan (grab the plane and move it); Shift+drag or "Box zoom" mode = zoom box
      this.svg.addEventListener('pointerdown', (ev) => {
        if (ev.button !== 0) return;
        const p = pos(ev);
        if (!inside(p)) return;
        const box = (this.dragMode === 'box') !== ev.shiftKey;
        drag = { kind: box ? 'box' : 'pan', p0: p, p1: p, xd: this.xd.slice(), yd: this.yd.slice(), W: this.W, H: this.H };
        this.svg.setPointerCapture(ev.pointerId);
        if (!box) { this.svg.classList.add('panning'); [this].concat(this.linked).forEach(c => { c._dragging = true; }); }
        hideHover();
        ev.preventDefault();
      });
      this.svg.addEventListener('pointermove', (ev) => {
        if (!drag) {
          if (ev.pointerType !== 'mouse') return;
          hov = pos(ev);                                   // several moves per frame draw one tooltip
          if (!hraf) hraf = requestAnimationFrame(() => { hraf = 0; if (hov && !drag) this._hover(hov); });
          return;
        }
        if (drag.kind === 'pan') {
          drag.p1 = pos(ev);
          if (raf) return;
          raf = requestAnimationFrame(() => {
            raf = 0;
            if (!drag) return;
            const dx = drag.p1[0] - drag.p0[0], dy = drag.p1[1] - drag.p0[1];
            this._setView({ x: Chart._shift(drag.xd, -dx / drag.W, this.opts.xlog), y: Chart._shift(drag.yd, dy / drag.H, this.opts.ylog) });
          });
          return;
        }
        const p = clamp(pos(ev));
        drag.p1 = p;
        const [x0, y0] = drag.p0;
        const dx = Math.abs(p[0] - x0), dy = Math.abs(p[1] - y0);
        const xOnly = dy < 10 && dx > 10, yOnly = dx < 10 && dy > 10;
        const bx = xOnly || !yOnly ? Math.min(x0, p[0]) : this.m.l, bw = xOnly || !yOnly ? dx : this.W;
        const by = yOnly || !xOnly ? Math.min(y0, p[1]) : this.m.t, bh = yOnly || !xOnly ? dy : this.H;
        Object.entries({ x: bx, y: by, width: bw, height: bh, visibility: 'visible' }).forEach(([k, v]) => this.box.setAttribute(k, v));
      });
      const end = () => {
        if (!drag) return;
        const d = drag;
        drag = null;
        this.svg.classList.remove('panning');
        if (raf) { cancelAnimationFrame(raf); raf = 0; }
        [this].concat(this.linked).forEach(c => { c._dragging = false; });
        if (Math.abs(d.p1[0] - d.p0[0]) + Math.abs(d.p1[1] - d.p0[1]) < 4) {       // a click, not a drag
          this.box.setAttribute('visibility', 'hidden');
          // two clicks close together in time and place = double-click: reset the view.  Detected here
          // because the native dblclick event is not reliable once pointer events are captured.
          const now = performance.now(), lc = this._lastClick;
          if (lc && now - lc.t < 450 && Math.hypot(lc.p[0] - d.p0[0], lc.p[1] - d.p0[1]) < 10) {
            this._lastClick = null; this._resetAt = now; this.reset(); return;
          }
          this._lastClick = { t: now, p: d.p0 };
          if (d.kind === 'pan' && this.opts.onClick) this.opts.onClick(this.ix(d.p0[0]), this.iy(d.p0[1]));
          else if (this.view) this.render();
          return;
        }
        if (d.kind === 'pan') {
          const dx = d.p1[0] - d.p0[0], dy = d.p1[1] - d.p0[1];
          this._setView({ x: Chart._shift(d.xd, -dx / d.W, this.opts.xlog), y: Chart._shift(d.yd, dy / d.H, this.opts.ylog) });
          return;
        }
        const [x0, y0] = d.p0, [x1, y1] = d.p1;
        this.box.setAttribute('visibility', 'hidden');
        const dx = Math.abs(x1 - x0), dy = Math.abs(y1 - y0);
        if (dx < 10 && dy < 10) return;
        const cur = this.view || {};
        const v = { x: cur.x || null, y: cur.y || null };
        const nx = [this.ix(Math.min(x0, x1)), this.ix(Math.max(x0, x1))];
        const ny = [this.iy(Math.max(y0, y1)), this.iy(Math.min(y0, y1))];
        if (dx >= 10 && dy >= 10) { v.x = nx; v.y = ny; }
        else if (dx >= 10) { v.x = nx; }
        else { v.y = ny; }
        this._setView(v);
      };
      this.svg.addEventListener('pointerup', end);
      this.svg.addEventListener('pointercancel', end);
      // Ctrl/⌘ + wheel (or trackpad pinch) zooms about the cursor; a plain wheel still scrolls the page
      this.svg.addEventListener('wheel', (ev) => {
        if (!(ev.ctrlKey || ev.metaKey)) return;
        const p = pos(ev);
        if (!inside(p)) return;
        ev.preventDefault();
        const f = Math.exp(Math.max(-1, Math.min(1, ev.deltaY * 0.004)));
        wz = { f: (wz ? wz.f : 1) * f, cx: (p[0] - this.m.l) / this.W, cy: (this.m.t + this.H - p[1]) / this.H };
        if (!wraf) wraf = requestAnimationFrame(() => { const z = wz; wz = null; wraf = 0; hideHover(); this.zoomBy(z.f, z.cx, z.cy); });
      }, { passive: false });
      this.svg.addEventListener('dblclick', () => { if (!(this._resetAt && performance.now() - this._resetAt < 600)) this.reset(); });
      this.svg.addEventListener('mouseleave', () => { hov = null; if (!drag) hideHover(); });
    }
    _hover(p) {
      const o = this.opts, m = this.m;
      if (p[0] < m.l || p[0] > m.l + this.W || p[1] < m.t || p[1] > m.t + this.H) {
        this.tip.style.display = 'none'; this.cross.setAttribute('visibility', 'hidden'); this.hl.innerHTML = ''; this._syncPeers(null); return;
      }
      this.hl.innerHTML = '';
      if (PREFS.crosshair === 'xy') this._crosshair(p);
      const rows = [];
      const hm = this._visible().filter(s => s.type === 'heatmap');
      this._syncPeers(hm.length ? null : this.ix(p[0]));       // related plots show the cursor at the same x
      if (hm.length) {
        // markers (annotations with a tip, scatter points) win when close; otherwise the cell under the cursor
        let near = null;
        for (const a of (o.annotations || [])) {
          if (a.type !== 'point' || !a.tip) continue;
          const d = Math.hypot(this.sx(a.x) - p[0], this.sy(a.y) - p[1]);
          if (d < 12 && (!near || d < near.d)) near = { d, a };
        }
        this.cross.setAttribute('visibility', 'hidden');
        if (near) { this._showTip(near.a.tip, this.sx(near.a.x), this.sy(near.a.y)); return; }
        const s = hm[hm.length - 1], [xe, ye] = hmEdges(s, o);
        const ix = findCell(xe, this.ix(p[0])), iy = findCell(ye, this.iy(p[1]));
        if (ix < 0 || iy < 0) { this.tip.style.display = 'none'; return; }
        const X0 = this.sx(xe[ix]), X1 = this.sx(xe[ix + 1]), Y0 = this.sy(ye[iy]), Y1 = this.sy(ye[iy + 1]);
        el('rect', { x: Math.min(X0, X1), y: Math.min(Y0, Y1), width: Math.abs(X1 - X0), height: Math.abs(Y1 - Y0), fill: 'none', stroke: css('--ink'), 'stroke-width': 1.5 }, this.hl);
        this._showTip(s.tip ? s.tip(ix, iy) : fmt(s.z[iy][ix]), p[0], p[1]);
        return;
      }
      const scatterMode = o.hover === 'nearest' || this._visible().every(s => s.type === 'scatter');
      if (scatterMode) {
        let best = null;
        for (const s of this._visible()) {
          for (let i = 0; i < (s.x || []).length; i++) {
            if (!num(s.x[i]) || !num(s.y[i])) continue;
            const X = this.sx(s.x[i]), Y = this.sy(s.y[i]);
            if (!isFinite(X) || !isFinite(Y)) continue;
            const d = Math.hypot(X - p[0], Y - p[1]);
            if (d < 18 && (!best || d < best.d)) best = { d, s, i, X, Y };
          }
        }
        for (const a of (o.annotations || [])) {
          if (a.type !== 'point' || !a.tip) continue;
          const X = this.sx(a.x), Y = this.sy(a.y), d = Math.hypot(X - p[0], Y - p[1]);
          if (d < 18 && (!best || d < best.d)) best = { d, a, X, Y };
        }
        this.cross.setAttribute('visibility', 'hidden');
        if (!best) { this.tip.style.display = 'none'; return; }
        el('circle', { cx: best.X, cy: best.Y, r: 7, fill: 'none', stroke: css('--ink'), 'stroke-width': 1.2 }, this.hl);
        let html = '';
        if (best.a) html = best.a.tip;
        else {
          const s = best.s, i = best.i;
          html = s.tipAt ? s.tipAt(i) : (s.tips && s.tips[i]) ? s.tips[i] : `<b>${s.name}</b><br>${o.xname || 'x'}: ${fmt(s.x[i])}<br>${o.yname || 'y'}: ${fmt(s.y[i])}`;
        }
        this._showTip(html, best.X, best.Y);
        return;
      }
      const xv = this.ix(p[0]);
      this.cross.setAttribute('x1', p[0]); this.cross.setAttribute('x2', p[0]); this.cross.setAttribute('visibility', PREFS.crosshair === 'x' ? 'visible' : 'hidden');
      let xshow = null;
      const pts = [];
      for (const s of this._visible()) {
        if (s.type === 'scatter' || s.nohover) continue;
        const xs = s.x, n = xs.length;
        if (!n) continue;
        let i, yv, X, Y, snap = true;
        if (s.sortedX === false) {
          let bd = Infinity; i = 0;
          for (let k = 0; k < n; k++) { const d = Math.abs(this.sx(xs[k]) - p[0]) + Math.abs(this.sy(s.y[k]) - p[1]) * 0.3; if (d < bd) { bd = d; i = k; } }
          yv = s.y[i];
          if (!num(yv)) continue;
          X = this.sx(xs[i]); Y = this.sy(yv);
          if (Math.abs(X - p[0]) > 40) continue;
        } else {
          const a = this._at(s, xv);
          if (!a) continue;
          ({ i, v: yv, X, Y, snap } = a);
        }
        el('circle', { cx: X, cy: Y, r: snap ? 4.5 : 3.5, fill: snap ? s.color : css('--plot-bg'), stroke: snap ? css('--surface') : s.color, 'stroke-width': 1.5 }, this.hl);
        pts.push({ X, Y, v: yv, s, i });
        // the value box lists computed points only, i.e. when the cursor is on a sample (the tags show the rest)
        if (!snap) continue;
        if (xshow === null) xshow = xs[i];
        rows.push(`<div class="r"><i style="background:${s.color}"></i>${s.name}<b class="tnum">${o.yfmt ? o.yfmt(yv, s, i) : fmt(yv)}${s.unit ? ' ' + s.unit : ''}</b></div>`);
      }
      if (PREFS.crosshair === 'xy') this._valueTags(pts);
      if (!rows.length) { this.tip.style.display = 'none'; return; }
      const head = `<div class="h">${o.xname || ''} <b class="tnum">${o.xfmt ? o.xfmt(xshow) : fmt(xshow)}</b>${o.xunit ? ' ' + o.xunit : ''}</div>`;
      this._showTip(head + rows.slice(0, 14).join('') + (rows.length > 14 ? `<div class="r">+${rows.length - 14} more</div>` : ''), p[0], p[1]);
    }
    /** Full crosshair: lines through the pointer and its x / y values read out on the axes. */
    _crosshair(p) {
      const m = this.m, W = this.W, H = this.H, ink = css('--plot-ink') || '#262626', bg = css('--plot-bg') || '#fff';
      const dash = { stroke: css('--muted') || '#898781', 'stroke-width': 1, 'stroke-dasharray': '4 3' };
      el('line', Object.assign({ x1: p[0], x2: p[0], y1: m.t, y2: m.t + H }, dash), this.hl);
      el('line', Object.assign({ x1: m.l, x2: m.l + W, y1: p[1], y2: p[1] }, dash), this.hl);
      const xv = this.ix(p[0]), yv = this.iy(p[1]);
      this._badge(Math.max(m.l + 20, Math.min(m.l + W - 20, p[0])), m.t + H + 15, this.opts.xfmt ? this.opts.xfmt(xv) : fmt(xv), 'middle', ink, bg);
      this._badge(m.l - 6, p[1] + 4, fmt(yv), 'end', ink, bg);
    }
    /** A value label on an axis: text on a filled rounded box (TradingView-style). */
    _badge(x, y, text, anchor, fill, ink) {
      const t = el('text', { x, y, 'font-size': 11, 'text-anchor': anchor, fill: ink, class: 'tnum', 'font-weight': 600 }, this.hl);
      t.textContent = text;
      let b; try { b = t.getBBox(); } catch (e) { return; }
      const r = el('rect', { x: b.x - 4, y: b.y - 2, width: b.width + 8, height: b.height + 4, rx: 3, fill }, this.hl);
      this.hl.insertBefore(r, t);
    }
    /** Where each curve meets the cursor: a dotted line to the right edge and its value there in the curve's colour
     *  (labels pushed apart so they never overlap). */
    _valueTags(pts) {
      const m = this.m, H = this.H, xr = m.l + this.W;
      const list = pts.filter(q => q.Y >= m.t && q.Y <= m.t + H).slice(0, 10).sort((a, b) => a.Y - b.Y);
      if (!list.length) return;
      for (const q of list) el('line', { x1: q.X, x2: xr, y1: q.Y, y2: q.Y, stroke: q.s.color, 'stroke-width': 1, 'stroke-dasharray': '2 3', opacity: 0.85 }, this.hl);
      let prev = -Infinity;
      for (const q of list) { q.by = Math.max(q.Y, prev + 15); prev = q.by; }
      let lim = m.t + H - 6;
      for (let k = list.length - 1; k >= 0; k--) { list[k].by = Math.min(list[k].by, lim); lim = list[k].by - 15; }
      const o = this.opts;
      for (const q of list) this._badge(xr - 3, q.by + 4, (o.yfmt ? o.yfmt(q.v, q.s, q.i) : fmt(q.v)), 'end', q.s.color, inkOn(q.s.color));
    }
    /** Charts that show the same x quantity (same name, unit and scale, or linked): they share the cursor. */
    _peers() {
      const o = this.opts, out = new Set(this.linked);
      const key = (o.xname || o.xunit) && o.cursorSync !== false ? [o.xname, o.xunit, !!o.xlog].join('|') : null;
      if (key) for (const c of ALL) {
        if (c === this || !c.host.isConnected || c.host.offsetParent === null || c.opts.cursorSync === false) continue;
        if ([c.opts.xname, c.opts.xunit, !!c.opts.xlog].join('|') === key) out.add(c);
      }
      return [...out];
    }
    _syncPeers(xv) {
      if (xv === null || xv === undefined) { (this._peerSet || []).forEach(c => c._sync(null)); this._peerSet = null; return; }
      if (!this._peerSet) this._peerSet = this._peers();
      this._peerSet.forEach(c => c._sync(xv));
    }
    /** Cursor shown in this chart because the pointer is on a related one: vertical line at x, the x value on the axis,
     *  and where each curve meets it. */
    _sync(xv) {
      if (!this.hl || !this.xd || this._dragging) return;
      this.hl.innerHTML = ''; this.tip.style.display = 'none';
      if (this.cross) this.cross.setAttribute('visibility', 'hidden');
      if (xv === null || xv === undefined || PREFS.crosshair === 'off' || (this.opts.xlog && xv <= 0)) return;
      const m = this.m, H = this.H, X = this.sx(xv);
      if (!(X >= m.l && X <= m.l + this.W)) return;
      el('line', { x1: X, x2: X, y1: m.t, y2: m.t + H, stroke: css('--muted') || '#898781', 'stroke-width': 1, 'stroke-dasharray': '4 3' }, this.hl);
      if (this._visible().some(s => s.type === 'heatmap')) return;
      const pts = [];
      for (const s of this._visible()) {
        if (s.nohover || s.sortedX === false || !(s.x || []).length) continue;
        const a = this._at(s, xv);
        if (!a || a.Y < m.t || a.Y > m.t + H) continue;
        el('circle', { cx: a.X, cy: a.Y, r: a.snap ? 4 : 3.5, fill: a.snap ? s.color : css('--plot-bg'), stroke: a.snap ? css('--surface') : s.color, 'stroke-width': 1.5 }, this.hl);
        pts.push({ X: a.X, Y: a.Y, v: a.v, s, i: a.i });
      }
      if (PREFS.crosshair === 'xy') {
        const ink = css('--plot-ink') || '#262626', bg = css('--plot-bg') || '#fff';
        this._badge(Math.max(m.l + 20, Math.min(m.l + this.W - 20, X)), m.t + H + 15, this.opts.xfmt ? this.opts.xfmt(xv) : fmt(xv), 'middle', ink, bg);
        this._valueTags(pts);
      }
    }
    /** The value of series s at x: the sample itself when one is within a few pixels (snap), otherwise read off the line
     *  between the two samples around x (lines only); null outside the data or between unconnected points. */
    _at(s, xv) {
      const xs = s.x, ys = s.y, n = xs.length, o = this.opts, X = this.sx(xv);
      const i = Chart._idxAt(xs, xv), Xi = this.sx(xs[i]);
      if (num(ys[i]) && (!o.ylog || ys[i] > 0) && Math.abs(Xi - X) <= (s.type === 'scatter' ? 6 : 12)) return { i, v: ys[i], X: Xi, Y: this.sy(ys[i]), snap: true };
      if (s.type === 'scatter' || n < 2) return null;
      const j = (xs[i] <= xv) === (xs[n - 1] >= xs[0]) ? i : i - 1;          // bracket [j, j+1] around x
      if (j < 0 || j >= n - 1) return null;
      const x0 = xs[j], x1 = xs[j + 1], y0 = ys[j], y1 = ys[j + 1];
      if (!num(y0) || !num(y1) || x1 === x0 || (xv - x0) * (xv - x1) > 0) return null;
      const t = (xv - x0) / (x1 - x0);
      const v = o.ylog ? (y0 > 0 && y1 > 0 ? Math.exp(Math.log(y0) + t * (Math.log(y1) - Math.log(y0))) : null) : y0 + t * (y1 - y0);
      if (!num(v)) return null;
      return { i: t < 0.5 ? j : j + 1, v, X, Y: this.sy(v), snap: false };
    }
    /** Index of the sample nearest to x in an ordered array. */
    static _idxAt(xs, xv) {
      const n = xs.length; let lo = 0, hi = n - 1;
      const asc = xs[n - 1] >= xs[0];
      while (hi - lo > 1) { const mid = (lo + hi) >> 1; if ((xs[mid] < xv) === asc) lo = mid; else hi = mid; }
      return Math.abs(xs[lo] - xv) < Math.abs(xs[hi] - xv) ? lo : hi;
    }
    _showTip(html, X, Y) {
      const t = this.tip;
      t.innerHTML = html; t.style.display = 'block';
      const w = t.offsetWidth, hh = t.offsetHeight, W0 = this.wrap.clientWidth;
      let left = X + 14, top = Y - hh - 10;
      if (left + w > W0 - 4) left = X - w - 14;
      if (top < 2) top = Y + 14;
      t.style.left = Math.max(2, left) + 'px'; t.style.top = top + 'px';
    }

    // ---------------------------------------------------------------- export
    toSVGString() {
      const clone = this.svg.cloneNode(true);
      clone.setAttribute('xmlns', NS);
      [...clone.querySelectorAll('.lb-overlay, .lb-zoombox')].forEach(n => n.remove());
      // legend -> svg text rows at the bottom
      const ser = (this.opts.series || []).filter(s => !s.nolegend && !this.hidden.has(s.name));
      const H0 = +clone.getAttribute('height');
      let extra = 0;
      if (ser.length > 1 && !this.opts.colorbar) {
        const perRow = Math.max(1, Math.floor(+clone.getAttribute('width') / 190));
        const rowsN = Math.ceil(ser.length / perRow);
        extra = rowsN * 18 + 8;
        ser.forEach((s, k) => {
          const x = 12 + (k % perRow) * 190, y = H0 + 14 + Math.floor(k / perRow) * 18;
          el('line', { x1: x, x2: x + 22, y1: y - 4, y2: y - 4, stroke: s.color, 'stroke-width': 2.5, 'stroke-dasharray': DASH[s.dash] || null }, clone);
          const t = el('text', { x: x + 28, y, 'font-size': 11, fill: css('--ink2') }, clone); t.textContent = s.name;
        });
      }
      const title = this.opts.title || '';
      const top = title ? 24 : 0;
      if (top || extra) {
        const g = el('g', { transform: `translate(0,${top})` });
        while (clone.firstChild) g.appendChild(clone.firstChild);
        el('rect', { x: 0, y: -top, width: clone.getAttribute('width'), height: H0 + top + extra, fill: css('--card') }, clone);
        clone.appendChild(g);
        if (title) { const t = el('text', { x: 12, y: 16, 'font-size': 13, 'font-weight': 600, fill: css('--ink') }, clone); t.textContent = title; }
        clone.setAttribute('height', H0 + top + extra);
        clone.setAttribute('viewBox', `0 0 ${clone.getAttribute('width')} ${H0 + top + extra}`);
      }
      clone.setAttribute('font-family', 'system-ui, -apple-system, Segoe UI, sans-serif');
      return new XMLSerializer().serializeToString(clone);
    }
    exportPNG(name) {
      const s = this.toSVGString();
      const img = new Image();
      const w = +this.svg.getAttribute('width');
      img.onload = () => {
        const c = document.createElement('canvas');
        c.width = img.width * 2; c.height = img.height * 2;
        const ctx = c.getContext('2d'); ctx.scale(2, 2); ctx.drawImage(img, 0, 0);
        c.toBlob((b) => download(b, (name || 'chart') + '.png'));
      };
      img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(s);
      return w;
    }
    exportSVG(name) { download(new Blob([this.toSVGString()], { type: 'image/svg+xml' }), (name || 'chart') + '.svg'); }
    exportCSV(name) { download(new Blob([this.toCSV()], { type: 'text/csv' }), (name || 'chart') + '.csv'); }
    toCSV() {
      const rows = [];
      for (const s of this._visible()) {
        if (s.type === 'heatmap') {
          rows.push(`# ${s.name}: rows = ${this.opts.yname || 'y'}, columns = ${this.opts.xname || 'x'}`);
          rows.push([''].concat(s.xs).join(','));
          s.ys.forEach((yv, iy) => rows.push([yv].concat(s.z[iy].map(v => (v === null || v === undefined ? '' : v))).join(',')));
          continue;
        }
        rows.push(`# ${s.name}`);
        rows.push(`${this.opts.xname || 'x'}${this.opts.xunit ? ' [' + this.opts.xunit + ']' : ''},${this.opts.yname || 'y'}${s.unit ? ' [' + s.unit + ']' : ''}`);
        for (let i = 0; i < s.x.length; i++) rows.push(`${s.x[i]},${s.y[i]}`);
      }
      return rows.join('\n');
    }
  }
  function download(blob, name) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = name;
    document.body.appendChild(a); a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 500);
  }
  function linkX(charts) { charts.forEach(c => { c.linked = charts.filter(o => o !== c); }); }

  window.LB = window.LB || {};
  Object.assign(window.LB, { Chart, linkX, fmt, download, markerPath, DASH, prefs: PREFS, setPrefs });
})();
