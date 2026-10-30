# LinBench — code architecture

How the code is organised, how data flows through it, and where to make a given kind of change.
Physics and maths are in [PHYSICS.md](PHYSICS.md); how to use the app is in [USER_GUIDE.md](USER_GUIDE.md).

> **Keep this file current.** Any change to a module's responsibility, a new module, a new API
> endpoint, a new panel or a new cache must be reflected here in the same change (see `CLAUDE.md`).

---

## 1. Big picture

```
 browser (index.html + app.js + chart.js)          Python process (python -m linbench)
 ┌───────────────────────────────────────┐  HTTP  ┌─────────────────────────────────────────────┐
 │ Setup screen → Run page → Results tabs │ ─────► │ server.py   ThreadingHTTPServer, JSON API    │
 │ LB.Chart (SVG, pan/zoom, heatmaps)     │ ◄───── │   └─ analysis.Session   (one per process)    │
 │ MOD: evaluates pole/residue models     │  JSON  │        ├─ discover  → group .lin files       │
 └───────────────────────────────────────┘        │        ├─ model     → OpModel (MBC, averaging)│
                                                   │        ├─ modal     → modes, Campbell         │
                                                   │        ├─ tf / lti  → responses, exports      │
                                                   │        ├─ structure → ctrb/obsv, Hankel       │
                                                   │        ├─ closedloop→ ROSCO model, margins    │
                                                   │        ├─ gainstudy → gain maps, scaling      │
                                                   │        └─ tuning / rosco_io → ROSCO inputs    │
                                                   └─────────────────────────────────────────────┘
```

* **Local only.** The server binds to `127.0.0.1`; the window (Qt WebEngine, or a browser) is just the
  UI. No data leaves the PC, and `offline.py` refuses any connection elsewhere.
* **Stateless front end, stateful back end.** One `Session` holds the loaded models; the page asks
  for results per operating point / channel pair and caches the JSON.
* **No heavy dependencies.** numpy, scipy, pyyaml on the Python side; no JS framework, no chart
  library (charts are hand-written SVG in `chart.js`).

## 2. Repository layout

| Path | What it is |
|---|---|
| `linbench/` | the application package (engine + web front end) |
| `linbench/web/` | `index.html`, `app.js`, `chart.js`, `app.css`, `guide.html` (in-app user guide) |
| `tests/` | validation checks (`run_all.py` writes `VALIDATION.md`) and sample generators |
| `samples/synthetic_rotor/` | synthetic 3-bladed rotor, 8 wind speeds × 8 azimuths, plus `synthetic_DISCON.IN` |
| `samples/rotor_speed_drift/` | synthetic set where 17 m/s was linearised while the rotor sped up by 3 % |
| `docs/` | this file, `PHYSICS.md`, `USER_GUIDE.md`, `CHANGELOG.md` |
| `main.py` | entry point of the exe (calls `linbench.__main__.main`) |
| `LinBench.spec`, `build.ps1`, `requirements.txt` | self-contained build with the Qt window (§5.4) |
| `tools/` | `make_icon.py`, `make_version_info.py`, `audit_bundle.py` (build helpers) |
| `assets/` | `linbench.ico` / `.png` (made by `tools/make_icon.py` during the build) |
| `VALIDATION.md` | latest validation results |
| `CLAUDE.md` | working rules for Claude sessions on this project (doc-update policy, tests) |

## 3. Python modules

Listed bottom-up (each depends only on the ones above it, except `analysis`/`server` at the top).

| Module | Responsibility | Key names |
|---|---|---|
| `linfile.py` | Parse OpenFAST `.lin` files by label (header fields, channel tables, `X: n x m` matrix banners). OpenFAST 2.x–4.x. Headers and parsed files are cached per (path, mtime, size); `read_many` reads in parallel threads. | `read_header`, `read_lin`, `read_many`, `clear_cache`, `LinFile`, `Channel` |
| `cache.py` | Disk cache (`~/.linbench/cache`, pickle): folder scan and processed models, keyed by file names/sizes/mtimes + options + code stamp. Newest 16 kept. Never raises. | `key`, `load`, `save` |
| `discover.py` | Find `.lin` files, group them into operating points **by header content** (wind speed; split only when azimuths repeat), order by azimuth, diagnose rotor-speed differences between azimuths. | `scan_folder`, `group_operating_points`, `OpGroup`, `rotor_speed_check` |
| `mbc.py` | Coleman / multi-blade transform: find blade triplets, build T(ψ), transform each azimuth file, average. | `MBCPlan`, `mbc_average`, `T1/T2/T3`, `find_triplets` |
| `model.py` | `OpModel`: one operating point = MBC-averaged (A,B,C,D) + labels + trim + per-file diagnostics; removes the azimuth state; unit conversion for display. | `OpModel`, `unit_convert`, `dof_of_state`, `fixed_label` |
| `modal.py` | Eigen-analysis, participation factors, mode labels, collective/whirl classification, Campbell tracking (MAC + Hungarian; `track_modes` is shared by open and closed loop). Results cached on the `OpModel` (`om._eig`, `om._eig_raw`, `om._struct_pos`). | `analyse_modes`, `campbell`, `track_modes`, `mode_table` |
| `lti.py` | Minimal state-space toolkit: block builders (LPF, notch, PI…), series/parallel, batched frequency response, exact ZOH step/impulse, **pole/residue export** for the browser. | `SS`, `freqresp`, `step_impulse`, `modal_export`, `FastSISO` |
| `tf.py` | SISO analysis of one input→output: residues, cancelled poles, zeros, DC gain, bandwidth, RHP zeros, time response, step metrics. | `SISO`, `zeros_of`, `freq_grid`, `step_metrics` |
| `structure.py` | Controllability/observability (orthogonal staircase / Kalman), PBH modal measures, Hankel singular values of the stable part. | `analyse`, `kalman`, `staircase`, `hankel_sv`, `stable_part` |
| `rosco_io.py` | Read DISCON.IN, tuning YAML, Cp-Ct-Cq table, `.fst` constants; resolve paths. | `read_discon`, `read_yaml`, `read_cp_table`, `discover_from_yaml` |
| `tuning.py` | ROSCO 1-DOF rotor model and PI tuning re-implemented (bit-exact vs ROSCO `tune_controller`). | `one_dof`, `tune`, `Surface`, `turbine_from_sources` |
| `closedloop.py` | `ControllerModel`: ROSCO signal chain from DISCON as an LTI system, closed around an `OpModel` (regions `pitch`, `torque`, `both`, `both_tq`); loop L(s), closed-loop A; margins (optionally with a loop delay), disk margin, Nyquist winding, root locus, pole labels, closed-loop modes for the Campbell diagram. | `ControllerModel`, `loop_analysis`, `disk_margin`, `nyquist_winding`, `root_locus`, `label_cl_poles`, `cl_modes` |
| `gainstudy.py` | Rebuild the controller with scaled gains; stability map, PI-scaling limits (bisection), trial-point comparison. | `with_gains`, `Metric`, `gain_map`, `stable_range`, `scaling_sweep`, `point` |
| `analysis.py` | `Session`: loading pipeline in a thread with progress + log; per-request methods behind the API; caches; `clean()` → JSON-safe. | `Session`, `clean` |
| `server.py` | HTTP server, static files, JSON routes, native file dialog helper, recent-folders list (`~/.linbench/recent.json`); single instance (`~/.linbench/server.json`, `/api/ping`, `/api/raise`); runs the server in a thread under the Qt window, or in the main thread with a browser. | `serve`, `existing_instance`, `Handler`, `browse`, `native_pick` |
| `window.py` | LinBench's own window: Qt WebEngine (PySide6), maximised, icon, Save dialog for downloads, raise on second start, `selftest` for `--window-test`. Optional: absent PySide6 → browser. | `available`, `run`, `selftest` |
| `offline.py` | Refuses connections off this PC and network name look-ups for the whole process; counts attempts. | `stay_on_this_pc`, `offline_works`, `BLOCKED` |
| `paths.py` | Bundle folder (source tree or `_internal` of the exe), web and asset paths, settings folder `~/.linbench`. | `bundle_dir`, `web_dir`, `asset`, `settings_dir` |
| `__main__.py` | CLI: `python -m linbench [--port] [--no-browser] [--browser] [--host]`; `--pick` runs the dialog helper process; `--window-test <url>` checks the window engine headlessly; installs the offline guard and the error log (`~/.linbench/linbench_error.log`). | `main` |

### 3.1 Loading pipeline (`Session._load`, background thread)

```
scan_folder ─► for each group: OpModel(files) ─► analyse_modes ─► campbell ─► (DISCON) _cl_row per op ─► _build_summary
   'scan'          'read'  (MBC, averaging,        'modes'          'campbell'        'closed'                 'summary'
                    azimuth removal, per-file
                    diagnostics)
```

* **Fast paths.** With `cfg['cache']` (set by the server, not by tests): if the disk cache has this
  folder (`cache.key('models', files, recursive, remove_azimuth)`), scan/read/modes/Campbell are
  skipped and only the log lines are regenerated. Otherwise all files are read in parallel
  (`read_many(full=True)`) before the per-point MBC, and the result is stored.
* **Two-step publication.** After the Campbell step the summary is built with `pending=True` and
  `progress['open_done']` is set: the page opens the results. The closed loop then runs in the same
  thread (wind speed nearest 17 m/s first), and the final summary replaces the first one
  (`summary['closed_pending']` false, `progress['done']`).
* `progress` = `{stage, pct, msg, elapsed, open_done, done, error, log[]}`; `_log(stage, text, **data)` appends
  a log entry whose `data` feeds the live charts on the Run page.
* `/api/progress?since=n` returns only log entries after `n` (plus `log_n`).
* The summary (`/api/summary`) is everything the results screen needs up front: `info`, `ops` (trim,
  periodicity, `omega_check`, `file_diag`, `az_sens`), `campbell`, `closed` rows, `design` rows,
  `controller`, `channels`, standard input/output indices.

### 3.2 Key objects

* **`OpModel`** — `A,B,C,D` (fixed frame, azimuth-averaged), `x/u/y` channels with `xrole/urole/yrole`
  (`''`, `col`, `cos`, `sin`, `rot`), labels, `x_dof` (dof, family, component), `wind`, `omega` (mean),
  `periodicity_raw/mbc`, `az_modes` (per-azimuth mode shifts), `file_diag` (per file Ω, pitch, torque…),
  `plan` (MBC triplets), `std_inputs()/std_outputs()`, `trim()`.
* **`ControllerModel`** — built from `(OpModel, DISCON dict, region)`. `build()` assembles `K`
  (measurements → actuator). `loop_system()` → `SS` of L(s); `closed_loop(k)` → `(A,B,C,D,states)` with
  every plant input as a disturbance and outputs = plant outputs + actuator command(s);
  `region` is always the MAIN loop (`pitch`/`torque`); `both=True` adds the other PI loop as the
  secondary feedback (`torque_fb` acting on `aux_input`), which stays closed when L is formed;
  `loop_eval(s)`, `ctrl_eval(s)` are vectorised evaluators. Gains live in attributes
  (`Kp, Ki, VS_Kp, VS_Ki, Kp_float`), so `gainstudy.with_gains` copies the object, edits them and calls `build()`.

### 3.3 Caching

| Where | Key | Cleared by |
|---|---|---|
| `om._eig`, `om._eig_raw` | per `OpModel` | new load |
| `Session.cms` | `(op, region)` → `ControllerModel` | new load |
| `Session._ccache` | op → `/api/closed` result | new load, region change for that op |
| `Session._gcache` | gain map / point / scaling, `('delay', tau, regions)`, `'interaction'` | new load, region change |
| `Session.campbell_cl` | closed-loop Campbell tracks | new load, region change |
| `linfile._HDR_CACHE / _LIN_CACHE` | (path, mtime, size) | parsed files dropped after each load (`clear_cache`) |
| disk `~/.linbench/cache/*.pkl` | `cache.key(kind, files, options)` incl. code stamp | automatically stale when any file or the code changes; newest 16 kept |
| browser `st.cache` | `'api:' + url + body` for cacheable endpoints; `'cl:'+op` for closed data | new results, region change |

## 4. HTTP API (`server.py`)

All responses are JSON produced by `analysis.clean()` (numpy → lists, NaN/inf → `null`,
complex → `[re, im]`, keys starting with `_` dropped).

| Method | Path | Body / query | Returns |
|---|---|---|---|
| GET | `/api/progress` | `since` | loading progress + new log entries |
| GET | `/api/summary` | — | results summary (see 3.1) |
| GET | `/api/recent`, `/api/browse` | `path` | recent inputs / folder listing |
| GET | `/api/modes` | `op` | mode table of one operating point |
| GET | `/api/campbell` | `all=1` or `closed=1` | tracks incl. hydro/aero states, or closed-loop tracks |
| GET | `/api/delay` | `tau` [s] | margins with a loop delay at every operating point |
| GET | `/api/ping`, `/api/version` | — | instance check; version, frozen, offline guard state |
| POST | `/api/raise` | — | a second start asks the open window to come to the front |
| POST | `/api/open_folder` | `path` (existing folder) | opens it in Explorer (status bar) |
| GET | `/api/interaction` | — | pitch and torque loops alone vs both closed, per operating point |
| GET | `/api/closed` | `op` | loop shape, Nyquist, poles, root locus, controller Bode, `L_modal`, `K_modal` |
| POST | `/api/inspect` | paths | resolved inputs (auto-found DISCON/YAML/Cp/fst) + scan |
| POST | `/api/pick` | `kind`, `start` | native file dialog result |
| POST | `/api/load` | config | starts the loading thread |
| POST | `/api/tf` | `op,input,output` | Bode, poles/zeros, metrics, `modal` |
| POST | `/api/time` | `op,input,output,kind,amp,T` | response + metrics + `modal` |
| POST | `/api/sweep` | `input,output` | magnitude at every op + `modal` per row |
| POST | `/api/structure`, `/api/structure_sweep` | `op, inputs[], outputs[]` | ranks, PBH measures, HSVs |
| POST | `/api/closed_time`, `/api/closed_tf` | channel pair, signal | closed vs open response + `cl_modal/ol_modal` |
| POST | `/api/gain_map` | `op,pair,lo,hi,n` | grids `zeta, zeta_sp, wn_sp, fn, max_re`, base, unaffected modes, YAML target |
| POST | `/api/gain_point` | `op,fx,fy,pair` | base vs trial: margins, poles, L/step exports + sampled steps |
| POST | `/api/gain_scaling` | — | ζ vs common PI factor at every op, stable range |
| POST | `/api/region` | `op, region` | recomputed closed-loop row for that op |

### 4.1 Pole/residue exports (`lti.modal_export`)

Frequency- and time-domain curves are sent as `{p, r, z, d, argk, off, phase_ok}`:
G(s) = d + Σ r/(s − p), only the upper member of each conjugate pair (`p.im > 0` ⇒ add the conjugate).
The browser (`MOD` in `app.js`) evaluates it over whatever window is visible, so curves never end
when panned. **Every export is verified on the server** against the state-space result (frequency
grid 1e-4…1e3 rad/s, and the time response if requested) and returned as `null` if it does not
match; the page then keeps the sampled arrays. Special cases: poles at the origin (PI integrators)
are always kept and excluded from the pruning scale; a pair that is not connected exports as G = 0.
Phase is continuous via the pole-zero form snapped onto arg G, branch fixed at 1e-3 rad/s.

## 5. Front end (`linbench/web`)

### 5.1 `chart.js` — `LB.Chart`

Dependency-free SVG chart, **shared with TuneMe** (TuneMe started from LinBench's copy; LinBench took
TuneMe's improved copy back in 1.1.0). Keep the two in step: a change made here should go to TuneMe's
`tuneme/web/chart.js` too, and the other way round. Engineering-plot axes: white plotting area in a
closed dark frame, ticks on all four sides, boxed legend inside the axes placed where it covers least. Options include `series[]` (line/scatter/heatmap), `xlog/ylog`, `equal`,
`annotations` (vline, hline, band, circle, point with label/tip), `colorbar`, `hover:'nearest'`,
`onClick(x,y)`, `dragMode`. Features:

* **Interaction:** drag = pan (both axes, log-aware), Ctrl/⌘+wheel or pinch = zoom at cursor,
  Shift+drag or box mode = zoom box, double-click = reset (two clicks within 450 ms and 10 px,
  detected in the pointer handler; the native `dblclick` is only a fallback); `LB.linkX` links x of several charts.
* **Function series:** a series with `fx(xDomain, n) → {x, y}` is re-sampled over the visible range
  on every render (450 points while dragging, 1600 at rest); its first view comes from `fx` itself.
* **Heatmaps:** `{type:'heatmap', xs, ys, z[iy][ix], color(v,ix,iy), tip(ix,iy)}`; cell edges at
  (geometric) mid-points; hover highlights the cell; CSV exports the grid.
* **Performance:** colour tokens cached per frame, width from `ResizeObserver` (no forced layout),
  pan redraw throttled with `requestAnimationFrame`; view limits clamp log axes to 1e-15…1e15.
* Export: `exportPNG/SVG/CSV`.
* **Page-wide plot preferences** (LinBench addition): `LB.setPrefs({lineScale, grid:'on'|'fine'|'off',
  legend:'auto'|'inside'|'side'|'top'|'off', legendMax, crosshair:'x'|'xy'|'off'})` stores them in
  `LB.prefs` and redraws every live chart (registry `ALL`, pruned of charts no longer in the page).
  Legend: beyond `legendMax` entries it gets an "N/M curves" head with *all / none*; in `auto` it is folded
  to that head until clicked (`_legOpen`); `side` puts it in a scrolling column right of the plot
  (`.lb-chart.lb-side` grid). Entries: click hides, two quick clicks show only that curve (again: all;
  detected in the click handler because the legend is rebuilt between the clicks), hover sets `_hi` and
  fades the other curves. `_crosshair(p)` draws the full crosshair with axis read-outs (default
  `crosshair:'xy'`); `_valueTags` adds, per curve, a dotted line to the right edge and its value
  (`_badge`, text colour from `inkOn`); `_at(s, x)` gives a curve's value at x (snaps to a sample within
  12 px, else interpolates between samples). **Shared cursor:** `_peers()` = linked charts plus every live,
  visible chart with the same `xname`/`xunit`/`xlog` (opt out with `cursorSync:false`); `_syncPeers(x)`
  calls `_sync(x)` on them while the pointer moves and clears them when it leaves. The plotting area
  uses `--plot-bg`. Pointer positions are scaled by svg width / rect width, so charts stay exact when the
  workspace is zoomed (text-size setting).

### 5.2 `app.js` — sections, in file order

1. `api()` with the result cache; state `st` (op, tab, sub-tab (remembered), frequency unit, channel choices, gain-study options).
   **Settings:** `PREF_DEF`/`prefs` (localStorage `lb.prefs`, only values that differ from the defaults),
   `applyPrefs` (data-* attributes on `<html>`, workspace zoom, `LB.setPrefs`, `st.freq`), `setPref`,
   `SETTINGS` (the rows of the Settings dialog), `dialog(title, build, foot)` (generic dialog in `#modal`),
   `openSettings`, `openShortcuts`, the ⋯ menu.
2. `MOD` (pole/residue evaluation: `freq`, `phase`, `time`), `fgrid`/`wgrid`, `fxFreq`, `fxTime`.
3. Setup screen: inspect/auto-fill, scan table (Ω spread), file pickers, recent list, Run.
4. Run page: `STAGES`, `MATH` (per-step explanation), `startProgress`, `progressUpdate` (steps, timings from log timestamps, live charts, log), `poll`.
5. Results: `renderInfo` builds the top-bar data-set pill and the specification sheet (`#infoKpis`
   headline tiles, `#infoGrid` sections of `.spec-row` label / value / source rows; `RS` maps ROSCO
   settings to words) shown by the pill's dialog. `buildInspector(P)` (right side panel, only while it is open: the view's top-level `.ctrls`
   rows are moved into it — right after the panel function's synchronous part and again when it
   finishes, kept in `P._ctrls`; *Insights* from `P._insights` (functions that fill an element:
   `tfInsights`, `loopInsights`, `summaryInsights`, built from `verdict(el, level, title, detail)` and
   `tiles(el, items)` with levels from `lvl(v, good, bad)`); *Results*: each card's own `.metrics` and `.notes` moved into groups (`P._groups`), cards
   left empty get `.is-empty`; plot quick options; card list; CSV export via `Chart.toCSV()`. A
   `MutationObserver` (reacting only to added cards / metrics / notes, not chart redraws) repeats this
   for content that arrives later; `inspTmr` is cleared on every rebuild), `fitSingle(P)` (one plot in the
   view: height = workspace), `enlarge(charts, …)` (all plots of a card), `SUBS` (views per group), `IC` (view icons), `renderNav` (left view list, closed-loop group
   disabled with a counter while pending: `navPending`), `setView(group, view)`, `renderInfo` (data-set
   pill in the top bar + the Turbine & model dialog content in `#infoStore`), operating-point chips with
   `gotoOp` (‹ › buttons, ← / → keys), `renderStrip`, `renderPanel`.
6. `PANELS` — one async function per view: `ol:schedule`, `ol:campbell`, `ol:modes`, `ol:tf`, `ol:time`,
   `ol:sweep`, `ol:struct`, `cl:summary`, `cl:loop`, `cl:nyquist`, `cl:poles`, `cl:gains`, `cl:cltime`, `cl:ctrl`.
7. Helpers: `renderAzimuthDebug`, gain-study colour bins (`ZBINS`, `zColor`, `zLegend`), `contour` (marching squares), `closedData`, `clMetrics`…
8. `buildReport()` + **Report** button: fetches the cached results it needs, draws charts off screen
   with `LB.Chart` and `toSVGString()`, and assembles one self-contained HTML page (inline CSS, SVG).
9. `refreshSummary(s)`: applies the final summary when the closed loop arrives after the results opened.
10. Command palette (`palCommands`, `palFilter`, `palRun`; Ctrl+K): views (with search keywords), wind
    speeds, actions and quick settings. Keyboard handler: Ctrl+K, Ctrl+, (settings), Ctrl+B (view list), ?, ← / →.

`card(parent, title, sub, opts)` builds a card with tool buttons (+, −, Box zoom, Reset, PNG, SVG, CSV),
a metric strip, an extra area and a chart host; `chartIn(card, opts)` creates the chart.

### 5.3 Styling

`app.css` defines colour tokens on `:root` — light by default (TuneMe's design language): series colours
`--s1…--s8` in MATLAB order, plot tokens `--plot-bg/--plot-frame/--plot-ink/--plot-grid`, status
`--good/--warn/--crit`, layout `--top-h/--sb-h/--nav-w/--gap`. Settings switch blocks keyed on attributes of
`<html>`: `data-theme=dark` (all tokens), `data-palette=cb|vivid`, `data-contrast=high`,
`data-density=compact|comfortable`, `data-motion=reduce`, `data-tools=always`; `body.nav-min` (icon-only
list), `body.no-nav` (setup and run screens). A small script in `index.html` sets the theme before the first
paint.

**Shell** (`index.html`): fixed top bar `.top` (☰ `#btnNav`, brand, data-set pill `#hdrTurbine`, search
`#btnCmd`, icon buttons `#btnInsp` (side panel `aside#inspector`, `body.insp-off`), `#btnReport`, `#btnSettings`, ⋯ `#menu` with `#btnNew`, `#btnLog`, guide,
shortcuts); left `nav#sidenav`; `main#workspace` scrolls on its own (fixed between bar and status bar) and
holds the screens; in results a sticky `.ctxbar` (`.ctx-op` with `#opPrev`, `#opChips`, `#opNext`; `#opStrip`
with `.st-grp` groups of `.st` label/value blocks built by `renderStrip`)
above `#panel`; `#modal` (Enlarge, Settings, Turbine & model, shortcuts), `#palette`, `#browser`.
Chart cards (`.chart-card`) show their `.chart-tools` on hover; `enlarge()` in `app.js` opens a chart in
`#modal`; the status bar (`.statusbar`, `sbState`, `sbResults`) sits at the bottom. `guide.html` reuses `app.css`.

### 5.4 Build and packaging

`build.ps1` → `.venv-build` (uv, Python 3.12, pinned `requirements.txt`) → icon + version info → PyInstaller
(`LinBench.spec`, windowed, `main.py` entry; of Qt only the libraries the window loads, no QML, no
translations, English engine locales only — as TuneMe) → copies `README.md`, `docs`, `samples` and the whole
`source` into `dist\LinBench` → `tools/audit_bundle.py` (every .exe/.dll/.pyd may import only files of
the folder or Windows) → `dist\LinBench-<version>-win64.zip`. The version comes from `linbench/__init__.py`.

## 6. Tests (`tests/`)

`python tests/run_all.py` runs every section and **overwrites `VALIDATION.md`**. Sections marked
"needs ROSCO" are skipped unless `ROSCO_DIR` points at a ROSCO checkout (IEA-15MW example + ROSCO's own
MBC and tuning). Run single files directly while developing; see `CLAUDE.md` for the rule on
`VALIDATION.md`.

| File | Checks | ROSCO? |
|---|---|---|
| `test_engine.py` | whirl/collective on the synthetic rotor; TF residues/zeros; closed loop, Nyquist | partly |
| `test_discover.py` | grouping when Ω differs across azimuths (drift/ripple/scatter/two sets) | no |
| `test_mbc.py` | MBC vs ROSCO `fx_mbc3`, periodicity, file-name invariance | yes |
| `test_structure.py` | Kalman ranks on known structures, HSVs vs Gramians, PBH on the rotor | no |
| `test_modal.py` | pole/residue exports vs state space (open loop, loop L, controller K) | partly |
| `test_gains.py` | gain map vs Nyquist/eigenvalues, PI-scaling limit = gain margin | no |
| `test_robust.py` | delay margins, disk margin, both loops closed, closed-loop modes, disk cache | no |
| `test_tuning.py` | tuning re-implementation vs ROSCO `tune_controller` | yes |

Generators: `make_synthetic.py` (rotor + `lin_text`, `write_files`), `make_synthetic_discon.py`,
`make_drift_example.py`, `data/make_rosco_truth.py`.

## 7. How to add things

**A new analysis tab**
1. Engine function in the right module (or a new module) returning plain dicts/arrays.
2. `Session` method (cache it if it is expensive) and a route in `server.py`.
3. Add the endpoint to `CACHEABLE` in `app.js` if its result depends only on the request.
4. Add `[key, label]` to `SUBS`, an icon path to `IC`, search words to `alias` in `palCommands`, and a
   `PANELS['ol:key']` / `['cl:key']` function.
5. Frequency/time curves: include a `modal_export` and pass `fx: fxFreq(...)` / `fxTime(...)`.
6. A test in `tests/`, registered in `run_all.py`; update docs (this file, PHYSICS if new maths,
   USER_GUIDE + `guide.html`, CHANGELOG).

**A new controller block** — add a builder in `lti.py` if needed, insert it in
`ControllerModel.build()` / `_speed_filter()` / `_float_path()`, document the equation in PHYSICS §7.

**A new progress-page item** — call `self._log(stage, text, **data)` in `_load`; show `data` in
`progressCharts()`; update `MATH` if the maths of a step changed.

## 8. Conventions

* **Units:** matrices stay in OpenFAST units (rad, rad/s, N·m, rpm for GenSpeed…). Display conversion
  only through `model.unit_convert` (rad → deg, rad/s → deg/s). Controller works in rad/s of generator
  speed and rad of pitch.
* **Signs:** ROSCO gains are negative; the PI block is `lti.pi(-Kp, -Ki)` acting on the measurement.
  L(s) = −K·G so that the closed loop is 1 + L = 0.
* **Stability tolerance:** Re λ > 1e-7 counts as unstable (closed loop, gain study).
* **Never order by file name**; operating points come from header values.
* **Labels, not positions:** channels are found by description regexes (`find_input/find_output`).
* **Everything returned to the browser goes through `clean()`.**

## 9. Known limitations / technical debt

* One `Session` per process: two browser tabs share (and replace) the loaded data.
* `closedloop` models the controller in continuous time; no discretisation, saturation, set-point
  smoothing or wind-speed estimator.
* The azimuth-state removal assumes the ElastoDyn `DOF_GeAz` naming.
* Guide exists twice (`docs/USER_GUIDE.md` and `linbench/web/guide.html`); both must be updated.
* IEA-15MW validation needs a ROSCO checkout; without it only the synthetic checks run.
