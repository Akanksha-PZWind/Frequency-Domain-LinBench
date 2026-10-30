# Changelog

Newest first. One entry per change set: what changed, why, which files, which tests.
Version number in `linbench/__init__.py` (currently 1.0.1; not bumped for the entries below yet).

## 2026-10-15 — Insights for Loop shape and Stability summary; dashboard-style side panel; larger plots; specification sheet
* **Why:** the side panel read like an essay; plots looked small inside their cards; the turbine dialog
  cut long values off and listed raw ROSCO codes.
* **Insights** now appear as a verdict line (green / amber / red) plus tiles (label, value, unit, one line
  of context, coloured edge): Transfer function (rewritten from bullet text), **Loop shape** (gain / phase
  margin against target, crossover and loop time constant, peak sensitivity, delay margin in controller
  steps, disk margin, bandwidth and peak of T; the limiting margin) and **Stability summary** (verdict over
  all wind speeds, weakest wind speed, worst GM / PM / s_m / disk margin, least-damped closed-loop mode,
  pitch / torque point count, Nyquist check, delay result; clicking a tile goes to that wind speed).
  Levels are listed in `docs/PHYSICS.md`. Notes under Results are folded behind "ⓘ Notes".
* **Plots:** heights scaled by Settings → Plot height (Compact 1×, **Normal 1.3×**, Tall 1.6×; `heightScale`
  in `chart.js`, skipped for `fixedHeight`, e.g. Enlarge); tighter chart cards; the hover tools sit in the
  title row instead of over the data; cards in a row share their height.
* **Turbine & model** becomes a specification sheet: headline figures (rated power, rotor diameter, rated
  wind, rated rotor speed, operating points, model size, controller), then sections *Turbine* (with the
  source of every constant), *Operating range* (wind speeds, rotor speed, pitch, power, files and azimuths),
  *Linear model*, *ROSCO controller* (each setting named in words with its interpretation and the DISCON
  name; pitch / torque gain schedules; notches in rad/s and Hz), *Input files* (full paths, wrapped) and
  warnings.
* Files: `web/app.js` (`verdict`, `tiles`, `lvl`, `loopInsights`, `summaryInsights`, `tfInsights`,
  `renderInfo`, pref `plotH`), `web/chart.js` (`heightScale`, `fixedHeight`), `web/app.css`, docs.
* Tests: browser, `samples/synthetic_rotor`, 1600×900: all 14 views open; insight tiles 5 (TF), 8 (Loop
  shape), 8 (Stability summary; weakest 12 m/s at 78.6 % of target, amber); spec sheet 5 sections,
  43 rows; plot heights 1.3× (e.g. Operating points 299 px).

## 2026-10-15 — Plots in the middle, numbers on the right; transfer-function insights; Enlarge keeps Bode pairs
* **Why:** the middle mixed plots with rows of numbers and notes; Enlarge on a Bode card showed only the
  magnitude; a single plot (Nyquist) used a fraction of the screen.
* **Results in the side panel:** every card's numbers (`.metrics`) and notes move to *Results*, grouped
  under the card's title (click: jump to the card). A card left with nothing but its title is hidden. With
  the side panel closed everything stays on the cards (toggling re-renders the view). Sections of the panel
  fold, and their state is remembered (`lb.ins.<section>`).
* **Insights** (Transfer function view): G(s) in factored form with gain K, plain-language points (steady
  state, dominant mode, lightly damped poles, RHP-zero bandwidth limit, unstable poles, bandwidth, modes not
  in this channel pair) and foldable pole / zero tables. Other views can add insights through `P._insights`.
* **Enlarge** shows every plot of the card (Bode magnitude + phase, x linked) sharing the window height.
* **One-plot views** (Nyquist, Modes, Time response): the plot takes the workspace height, also after a resize.
* **Fix:** a pending side-panel update of the previous view could take the numbers of the new view
  (`inspTmr` is now cleared on every rebuild).
* Files: `web/app.js` (`enlarge`, `buildInspector`, `fitSingle`, `tfInsights`, `ol:tf`), `web/app.css`,
  `docs/PHYSICS.md` (insight rules), `docs/ARCHITECTURE.md`, `docs/USER_GUIDE.md`, `web/guide.html`.
* Tests: browser, `samples/synthetic_rotor`, 1600×900: all 14 views — no numbers left in the middle,
  all in Results (3–13 per view); Nyquist / Modes / Time response plots 561–622 px high; Enlarge on Bode
  gives magnitude and phase, linked; K checked by hand (G(0) = −0.135 = K·65/(0.0804·56.8)); closing the
  panel puts the numbers back; console clean.

## 2026-10-14 — Side panel on the right; tidier result numbers; tooltip only on data points
* **Why:** the settings rows, boxed numbers and bullet notes above the plots looked cluttered.
* **Side panel** (`#inspector`, icon in the top bar; open by default, remembered): *View settings* (the
  view's own controls — input/output, quick picks, channels, check boxes, max frequency — moved there
  from above the plots), *Plots* (legend, cursor, grid, line width), *In this view* (every card; click
  scrolls to it and outlines it briefly) and *Data* (one CSV with the curves of all plots of the view).
  Below 1150 px window width it floats over the plots.
* **Operating-point bar** spans the whole width: the wind-speed selector stretches, the value groups
  share the row below.
* **Numbers above plots:** label above value in columns divided by thin lines instead of boxes; a red
  dot marks a bad value. Explanations under a card are footnotes (small, above a thin line).
* **Tooltip** appears only when the cursor is on a computed point; between points only the coloured
  value tags show.
* Files: `web/index.html`, `web/app.css`, `web/app.js` (`buildInspector`, `renderPanel`, pref `insp`),
  `web/chart.js` (`toCSV`, tooltip rows only for snapped samples), docs.
* Tests: browser, `samples/synthetic_rotor`, 1600×900: all 14 views open and hover without errors, every
  panel-level settings row lands in the side panel, the Hankel plot shows no tooltip between points and one
  on a point, panel toggles, jump-to scrolls; console clean.

## 2026-10-14 — Operating-point bar redesigned; TradingView-style cursor shared by related plots
* **Why:** the bar above the plots repeated the view name ("Open loop Operating points", already in the
  view list) and listed the operating values as one line of text.
* **Operating-point bar:** two rows. Top row: the wind-speed selector (‹ segmented chips ›, labelled "Wind
  speed [m/s]"); below it, groups of values with a small label above each value: operating point (rotor, pitch, generator torque,
  power, TSR, Cp), controller (loop badge, Kp, Ki or VS gains) and design model (A, B). Unknown values are
  left out; each group explains itself on hover. The view name is no longer repeated (it stays in the
  window title).
* **Cursor (`chart.js`):** the full crosshair is now the default. Dotted lines in both axes, the x and y
  values on the axes, and for every curve a dot where it meets the cursor with a dotted line to the right
  edge and its value in the curve's colour (labels pushed apart). Plots that show the same x quantity
  (same x name, unit and scale, or linked with `LB.linkX`) follow the cursor together: vertical line, x
  value and curve values, e.g. all wind-speed plots of Operating points or Bode magnitude and phase. Between
  sparse samples (one per wind speed) the value is read off the line (hollow dot); within 12 px of a
  sample it snaps to it (filled dot). Settings → Crosshair: Full / Vertical / Off.
* Files: `web/chart.js` (`_at`, `_valueTags`, `_badge`, `_peers`, `_syncPeers`, `_sync`, `_idxAt`,
  `inkOn`, `cursorSync` option), `web/app.js` (`renderStrip`, crosshair default), `web/index.html`,
  `web/app.css`, `docs/ARCHITECTURE.md`, `docs/USER_GUIDE.md`, `web/guide.html`.
* Tests: in the browser on `samples/synthetic_rotor`: every view opened and hovered from script without
  errors; on Operating points the hovered pitch plot and the rotor-speed and torque plots show the same x
  with their curve values; Loop shape magnitude and phase share the cursor; console clean.

## 2026-10-14 — New interface: view list, Settings, dark theme, command palette, better legends
* **Why:** the header had grown to a row of text buttons (rad/s | Hz, Report, Analysis log, Guide, New
  analysis) and two rows of tabs; plots with many curves lost a large part of the axes to the legend.
* **Shell.** Fixed top bar with only the essentials: ☰, LinBench, the data set (dot = closed-loop verdict;
  click for the *Turbine & model* dialog, which replaces the fold-out info panel), a search box, icon
  buttons for Report and Settings, and a ⋯ menu (New analysis, Analysis log, Guide, Keyboard shortcuts).
  The Open/Closed tabs and the sub-tab pills became one **view list** on the left with icons (can shrink
  to icons only; the closed-loop group shows its counter while it is computed). The workspace uses the
  whole window and scrolls on its own; a sticky context bar holds the view name, the wind speeds (‹ ›)
  and the operating values. Setup and Run pages also use the full width.
* **Settings** (gear, Ctrl+,; stored in `localStorage` `lb.prefs`): frequency unit (moved from the top
  bar), theme light / dark / system, density, text-and-plot size, view list; legend style, folding limit,
  line width, grid (on / fine / off), crosshair (vertical / full with axis read-outs / off), plot tools on
  hover or always; colour-blind-safe (Okabe–Ito) or vivid palette, high contrast, reduced motion.
* **Command palette** (Ctrl+K): views (also by keywords such as bode, margins, hankel), wind speeds,
  actions and quick settings. Shortcuts: Ctrl+B view list, ? shortcut list, Esc, ← / →.
* **Legends** (`chart.js`): beyond 6 curves a legend folds to "▸ N/M curves" (click: scrolling list with
  all / none); "Beside" puts it in a column next to the plot; hovering an entry fades the other curves;
  a double-click shows that curve alone (again: all). Plot area colour from `--plot-bg` (dark theme);
  pointer positions corrected for the zoomed workspace.
* Removed: `#freqUnit`, `#mainTabs`, `#subTabs`, `#infoPanel` and their CSS (`.tabs`, `.subtabs`,
  `.opbar`, `details.info`).
* Files: `web/index.html`, `web/app.css`, `web/app.js`, `web/chart.js`, `web/guide.html`, `README.md`,
  `CLAUDE.md`, `docs/ARCHITECTURE.md`, `docs/USER_GUIDE.md`.
* Tests: in the browser on `samples/synthetic_rotor`: all 14 views open without console errors (light
  and dark); Settings rows apply at once and persist; legend fold / hover highlight / solo checked from
  script (8 Campbell tracks: hover fades the other curves, two clicks leave 1 of 8, two more all 8);
  palette finds a wind speed by number and views by keyword; full crosshair read-out equals the axis
  value under the pointer at 125 % text size; "Beside" legend lays out next to the plot. No engine change.
  `chart.js` changes are not yet in TuneMe's copy.

## 2026-10-14 — Aligned with TuneMe's final build; leftovers of older versions removed
* From TuneMe's final version: the spec keeps of Qt only the libraries the window loads (no QML, no
  debug packs, no translations, English engine locales) and filters binaries too — the first build had
  failed its self-contained check on `Qt6Multimedia.dll` / `qdirect2d.dll`, which are now left out;
  `tools/audit_bundle.py` knows the Windows libraries Qt WebEngine uses; the window self-test reads the
  page state as JSON text; if the own window cannot start, the page opens in the browser instead.
* Layout like TuneMe: `launcher.py` → `main.py`; one pinned `requirements.txt` (was also
  `requirements-build.txt`); README gains the "another PC" / SmartScreen notes.
* Removed (still in git history): `LinBench.bat`, `LinBench.command`, `build_exe.bat` (old launchers /
  build wrapper), `AUDIT.md` (review of the scripts LinBench replaced), `tests/e2e.js` (needed Node +
  Playwright of a cloud setup), the failed `build/` and `dist/`, `__pycache__`.

## 2026-10-14 — LinBench 1.1.0: TuneMe look and behaviour, own window, self-contained build
* **Look (shared with TuneMe).** One light look whatever the Windows theme (dark-mode block removed;
  TuneMe's colour tokens and MATLAB-order curve colours). `web/chart.js` replaced by TuneMe's copy of the
  shared chart library (closed dark frame, ticks on all four sides, boxed legend inside the axes, faster
  hover/wheel/legend, heat maps as one image), with LinBench's double-click fix put back. Plot tools appear
  while the pointer is on a plot; **Enlarge** shows a plot as large as the window (Esc closes). A **status bar**
  shows what is running (with percentage), the folder (click opens it in Explorer), the closed-loop verdict
  and the version.
* **Own window.** `linbench/window.py`: Qt WebEngine window (as TuneMe), maximised, LinBench icon; closing
  it ends LinBench; a second start brings the open window to the front (`~/.linbench/server.json`,
  `/api/ping`, `/api/raise`); exports and the report go through a Save dialog. Without PySide6 (running
  from source) the page opens in the default browser as before; `--browser` forces that.
* **Stays on this PC.** `linbench/offline.py`: connections from LinBench to anything but 127.0.0.1, and
  network name look-ups, are refused and counted (`/api/version`).
* **Error log** for the windowed exe: `~/.linbench/linbench_error.log`. `linbench/paths.py` for bundle and
  settings folders.
* **Build like TuneMe.** `build.ps1` (uv, Python 3.12, pinned `requirements-build.txt` incl. PySide6
  6.12), `LinBench.spec` (windowed, icon, version info, unused Qt parts and Qt translations left out),
  `tools/make_icon.py`, `tools/make_version_info.py`, `tools/audit_bundle.py` (fails the build if anything
  outside the folder or Windows is needed), and `dist\LinBench-<version>-win64.zip`. The folder also
  carries `docs`, `samples` and the complete `source`. `build_exe.bat` now runs `build.ps1`.
* Files: `web/app.css`, `web/chart.js`, `web/app.js`, `web/index.html`, `server.py`, `__main__.py`,
  `window.py`, `offline.py`, `paths.py` (new), build files, docs.
* Tests: all screens checked in the browser (light look forced against a dark browser, Enlarge, status
  bar, every tab); offline guard and the new endpoints checked from Python. **Not yet run:** the build
  and the Qt window (they need the PySide6/WebEngine download).

## 2026-10-07 — Speed, delay and disk margin, loop interaction, closed-loop Campbell, report
* **Speed.** Each `.lin` file is opened once and files are read in parallel (`linfile.read_many`,
  in-memory header/file cache). Processed models, modes and Campbell tracks are stored on disk
  (`cache.py`, `~/.linbench/cache`, keyed by file names/sizes/dates, options and code), so a folder
  analysed before reopens at once. Measured on `samples/synthetic_rotor`: fresh load 2.2 s → 1.0 s;
  repeat load: open-loop results after 0.04 s, 0.4 s in total with the closed loop.
* **Results sooner.** Open-loop results are published (`progress.open_done`, `summary.closed_pending`)
  before the closed loop is computed; the Closed loop tab shows a counter and switches on when ready.
  The selected wind speed is closed first.
* **Loop delay** (`loop_analysis(tau)`, `/api/delay`): margins with a pure delay, default 1.5 × DT when
  the `.fst` gives DT; stability from the Nyquist count. Input and dashed curves in Stability summary.
* **Disk margin** (`closedloop.disk_margin`): simultaneous gain and phase margin; summary metric,
  table column, loop-shape/Nyquist metric.
* **Pitch–torque interaction** (`ControllerModel` regions `both` / `both_tq`, `/api/interaction`):
  both PI loops closed together against each alone; card in Stability summary; region choice
  *Both loops (R2½)* makes every closed-loop tab use it.
* **Closed-loop Campbell** (`closedloop.cl_modes`, `modal.track_modes`, `/api/campbell?closed=1`):
  open/closed switch on the Campbell tab, open-loop tracks kept in grey, controller modes included.
* **One-click report** (`buildReport` in `app.js`, **Report** button): self-contained HTML with
  summary, inputs, warnings, operating points, Campbell diagrams, stability table, gain headroom and
  interaction; opens in a new tab and downloads; prints to PDF.
* **Double-click reset** made reliable (detected from two quick clicks, since the native event is
  not dependable with captured pointer events).
* Files: `linfile.py`, `cache.py` (new), `discover.py`, `analysis.py`, `server.py`, `closedloop.py`,
  `modal.py`, `web/app.js`, `web/chart.js`, `web/index.html`, `web/guide.html`, docs.
* Tests: `tests/test_robust.py` (new, 9 checks); all earlier non-ROSCO tests re-run.

## 2026-09-30 — Operating points: rotor-speed plot reworked, debugging text moved to the guide
* *Rotor speed of every azimuth file* → **Rotor speed across the azimuth files**. It now plots ΔΩ from
  each wind speed's mean in %, instead of absolute rpm, where a spread vanished against the 6–12 rpm
  wind range. Files are spread sideways in azimuth order and joined (drift = slope, ripple = wave),
  with dashed ±0.25 % warning lines and a shaded strip for the selected wind speed; edge points are no
  longer clipped.
* *Azimuth files* section: the explanation bullets were removed and the subtitle shortened; that
  guidance now lives in the guide (`guide.html` workflow section, `docs/USER_GUIDE.md` §7.3).
* Files: `web/app.js` (`ol:schedule`, `renderAzimuthDebug`), `web/guide.html`, `docs/USER_GUIDE.md`.

## 2026-09-30 — Documentation set
* Added `docs/ARCHITECTURE.md`, `docs/PHYSICS.md`, `docs/USER_GUIDE.md`, this changelog, and
  `CLAUDE.md` (working rules: which document to update for which change, test policy).

## 2026-09-30 — Rotor speed that differs between azimuths
* **Problem:** files at one wind speed but different rotor speeds were split into fragments when
  the spread was > 2 % (e.g. 3 + 3 + 2 azimuths, each averaged over a biased subset) and merged
  silently below 2 %.
* **Now:** grouping by wind speed; a wind speed is split only when azimuths repeat (two complete
  sets: by file root, then by rotor speed). The spread is classified as drift / periodic / scatter
  (adjusted R² of Ω vs simulation time and vs azimuth) with notice/warning levels, and each azimuth
  file's own MBC model is compared with the averaged one (Δf, Δζ per mode).
* **UI:** ±spread in the setup table; WARNING lines in the Run log; Operating points tab: rotor
  speed of every file, Ω vs simulation time, per-file table and per-azimuth mode shifts.
* Files: `discover.py`, `model.py`, `analysis.py`, `app.js`, `guide.html`; sample `samples/rotor_speed_drift`
  (`tests/make_drift_example.py`); `tests/make_synthetic.py` refactored (`lin_text`, `write_files`).
* Tests: `tests/test_discover.py` (new).

## 2026-09-29 — Gain study, live Run page, usage guide, speed
* **Gain study tab** (`gainstudy.py`): Kp × Ki (or PI × floating) stability map coloured by
  speed-loop damping or least-damped movable mode, stability boundary and ζ contour, click-to-compare
  trial gains (margins, poles, Nyquist, step responses), PI gain scaling across wind speeds with
  bisection-refined stable range and gain headroom.
* **Run page:** step list with timings, per-step maths, live charts (periodicity, modes, margins),
  detailed analysis log; reachable later via **Analysis log**.
* **In-app guide** (`web/guide.html`, **Guide** button); DISCON auto-detected in/next to the
  linearisation folder; ← / → change operating point; last sub-tab remembered; server caches for
  closed-loop results and gain studies.
* `samples/synthetic_rotor/synthetic_DISCON.IN` (`tests/make_synthetic_discon.py`): lets every
  closed-loop screen run without ROSCO.
* **Fix:** a PI integrator (pole at 0) inflated the pruning scale in `lti.modal_export`, so loop-shape
  exports were refused and plots fell back to fixed-range data.
* Tests: `tests/test_gains.py` (new); `test_modal.check_closed_synthetic`.

## 2026-09-29 — Curves that continue when panned; performance
* Frequency and time responses sent as verified pole/residue models (`lti.modal_export`) and
  evaluated in the browser for the visible range (Bode, sweep, loop L/S/T, controller, time
  responses); continuous phase from the pole-zero form.
* Batched `lti.freqresp` (~10× faster transfer functions); chart redraw without forced layout,
  cached colours, fewer points while dragging; client-side result cache; zoom limits.
* Tests: `tests/test_modal.py` (new).

## 2026-09-28 — Controllability, observability, Hankel singular values; pan
* `structure.py`: orthogonal-staircase ranks, uncontrollable/unobservable modes, PBH measures,
  modal Hankel values, HSVs of the stable part with balanced-truncation bound; new tab
  **Controllability & Hankel** including sweeps across wind speeds.
* Charts: drag pans (log-aware, linked axes), Ctrl/⌘+wheel and pinch zoom, + / − buttons,
  Shift+drag or Box zoom for the old zoom box.
* Tests: `tests/test_structure.py` (new).

## Before 2026-09-28 — LinBench 1.0.1 (initial version, built in an earlier session)
* Open loop: grouping by header content, MBC, azimuth averaging and state removal, participation-factor
  labels, collective/whirl classification, MAC Campbell tracking, transfer functions, time responses,
  sweeps. Closed loop: full ROSCO chain from DISCON, root-found margins, Nyquist Z = N + P, root locus,
  1-DOF design comparison, tuning re-implementation. See `AUDIT.md` and `VALIDATION.md`.
