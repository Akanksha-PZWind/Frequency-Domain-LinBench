# LinBench — user guide

How to run LinBench and read every screen. The same content is in the app (**Guide** button,
`linbench/web/guide.html`). Equations: [PHYSICS.md](PHYSICS.md). Code: [ARCHITECTURE.md](ARCHITECTURE.md).

> **Keep this file and `linbench/web/guide.html` in step.** Any visible change to a screen, control,
> default or workflow is documented in both (see `CLAUDE.md`).

---

## 1. Quick start

1. **Start LinBench**
   | | |
   |---|---|
   | Any Windows 10/11 PC | unzip `LinBench-<version>-win64.zip` (or copy the `LinBench` folder) and double-click `LinBench.exe` — nothing to install, no internet. First start: SmartScreen may ask, **More info → Run anyway** |
   | From source | `python -m linbench` (options `--port 8765`, `--browser`, `--no-browser`) |

   `LinBench.exe` opens as **its own window** (maximised, light, like TuneMe); closing the window
   stops LinBench, and starting it again while it is open brings the window to the front. Keep the
   whole `LinBench` folder together. Run from source without the build packages, LinBench opens in your
   browser instead and the console window is the server.
   LinBench keeps to your PC: its window talks to it over 127.0.0.1 and any other connection is refused.
   If something goes wrong, details are in `%USERPROFILE%\.linbench\linbench_error.log`.
2. Pick the **linearisation folder**. The table on the right lists the operating points found.
3. For closed loop pick the **DISCON.IN**. One in the linearisation folder (or one level up) is found
   automatically. A ROSCO **tuning YAML** fills the other paths and adds the ω_pc/ζ_pc targets.
4. Choose **Open loop**, **Closed loop** or **Both**, press **Run analysis**.
5. Pick an operating point with the wind-speed chips (or ← →) and browse the tabs.

**Samples** (synthetic, not a real turbine):
* `samples/synthetic_rotor` — rotor with blade DOFs + `synthetic_DISCON.IN`; every screen works on it.
* `samples/rotor_speed_drift` — 17 m/s linearised while the rotor sped up by 3 % (see §7.3).

## 2. Inputs

| Input | Needed for | Notes |
|---|---|---|
| Linearisation folder | everything | any file names; grouped and ordered by the wind speed, rotor speed and azimuth inside each file; tick *include sub-folders* if split |
| `DISCON.IN` | closed loop, gain study | gain schedule, filters, notches, floating feedback, actuator, torque mode |
| ROSCO tuning YAML | optional | fills paths, gives targets, tuned gains shown on the gain map |
| Cp-Ct-Cq table, `.fst` | optional | for the 1-DOF design comparison; usually found from DISCON/YAML |

**Remove rotor-azimuth state** (default on) drops the generator-azimuth position state after MBC, as ROSCO does.

The setup table shows, per operating point: wind speed, rotor speed (with **±spread** in red when the
azimuth files disagree), number of files, azimuth coverage, file names and notes.

## 3. The Run analysis page

* **Steps** with a tick and the time each took; click a step to see its maths.
* **What is being computed:** equations of the current step and why it matters.
* **Live charts:** periodicity before/after MBC per wind speed; modes found (frequency vs wind,
  colour = damping, red = unstable); closed-loop gain and phase margins (✕ = unstable).
* **Analysis log:** one line per result: model size, periodicity, least-damped mode, gains,
  STABLE/UNSTABLE with Z, P, N, margins, and red WARNING lines (e.g. rotor-speed drift).
* Results open automatically unless you click or scroll the page. **⋯ → Analysis log** brings it back.
* **Results open as soon as the open-loop part is ready.** The closed loop is computed afterwards
  (selected wind speed first); the **Closed loop** views in the list on the left show a counter and switch on when it is done.
* **Repeat runs are instant.** Processed results are stored in `~/.linbench/cache` and reused when no
  `.lin` file changed (name, size, date). Changing any file, or updating LinBench, recomputes.
  Delete that folder to clear it.

## 4. Moving around

The window has four parts:

* **Top bar:** ☰ (view names or icons only), the data set (dot = closed-loop verdict; click it for the
  specification sheet: headline figures, turbine constants with their source, operating range, linear
  model, ROSCO settings in words with their DISCON names, gain schedules, notches, input files), the search
  box (**Ctrl+K**), **Report** (page icon), **Settings** (gear, **Ctrl+,**) and **⋯** (New analysis,
  Analysis log, Guide, Keyboard shortcuts).
* **View list** on the left: the seven open-loop and seven closed-loop views. The closed-loop group is
  greyed out without a DISCON, and shows a counter while it is being computed.
* **Workspace:** at the top, a row with the wind speeds (‹ › or ← / →; the dot is the closed-loop verdict at
  that point) and below it that point's values in three groups: operating point (rotor, pitch, generator torque, power,
  TSR, Cp), controller (loop, Kp, Ki) and design model (A, B). The plots below scroll on their own.

* **Side panel** on the right (panel icon in the top bar shows / hides it); its sections fold and stay
  as you left them:
  * *View settings* — the view's own choices (input and output, quick picks, channels, check boxes, max frequency);
  * *Insights* — a verdict line (green / amber / red) and tiles with the figures that matter:
    *Transfer function*: G(s) in factored form, steady-state gain and sign, bandwidth, dominant and
    lightly damped modes, the bandwidth limit set by right-half-plane zeros, modes not in this pair, and
    the pole / zero lists; *Loop shape*: margins against their targets (6 dB, 45°, s_m 0.5), the limiting
    one, crossover and loop time constant, peak sensitivity, delay and disk margins, bandwidth and peak of
    T; *Stability summary*: stable or not at every wind speed, the weakest wind speed and the worst
    margins (click a tile to go to that wind speed), least-damped closed-loop mode, Nyquist check;
  * *Results* — the numbers of every plot, under the plot's name (click the name to jump to it); the
    explanations are folded under "ⓘ Notes";
  * *Plots* — legend, cursor, grid, line width; *In this view* — jump to any plot or table;
    *Data* — one CSV file with the curves of all plots in the view.

  The middle then shows only plots and tables; a view with a single plot (Nyquist, Modes, Time response)
  uses the full height. With the panel closed the numbers are shown on the cards again (a red dot marks a
  value that needs attention). In a narrow window the panel floats over the plots.

**Cursor.** Pointing at a plot shows dotted lines in both axes with the x and y values on the axes, and
for each curve its value at the cursor, labelled at the right edge in the curve's colour (a filled dot is a
computed point, a hollow dot a value read off the line between points). Plots of the same quantity on the
x axis follow along. The box with the values of all curves appears only when the cursor is on a
computed point; between points only the coloured tags show. Plots of the same quantity: on Operating points every wind-speed plot shows the same wind speed and its values;
Bode magnitude and phase share the frequency. Settings → Crosshair switches to a vertical line only, or off.
* **Status bar:** what is running (with percentage), the folder in use (click to open it), the
  closed-loop verdict and the version.

**Search (Ctrl+K)** finds a view (also by words such as *bode*, *margins*, *eigenvalues*, *hankel*),
a wind speed (type the number), an action (report, new analysis) or a quick setting (Hz, dark theme,
legends, crosshair). ↑ ↓ and Enter pick; Esc closes.

Move the pointer onto a plot and its tools appear: **Enlarge** (the plot — or both plots of a Bode
diagram — as large as the window; Esc or
**Close** returns), **+ / −**, **Box zoom**, **Reset**, **PNG / SVG / CSV**. Curves are named in a boxed
legend inside the axes. A plot with more curves than the limit in Settings (6) shows only
**▸ N/M curves**; click it to open a scrolling list with **all / none**.

| Action | How |
|---|---|
| Operating point | chips, ‹ ›, a table row, ← / →, or type the wind speed in Ctrl+K |
| Pan a plot | drag (mouse or touch); Bode, loop, controller and time curves are recomputed for the new range |
| Zoom | Ctrl/⌘ + wheel or pinch (about the cursor), or **+ / −** |
| Zoom box | Shift + drag, or **Box zoom**; flat box = x only, tall box = y only |
| Reset | double-click the plot, or **Reset** |
| Hide a curve | click its legend entry |
| Only one curve | double-click its legend entry (double-click again: all curves) |
| Find a curve | point at its legend entry: the other curves fade |
| Export | **PNG**, **SVG**, **CSV** on every plot (heatmaps export as a grid) |
| Whole analysis in one file | **Report** (page icon in the top bar; see §6.2) |
| Shortcuts | **?** or ⋯ → Keyboard shortcuts |

### 4.1 Settings (gear, Ctrl+,)

Saved on this PC and applied at once; **Restore defaults** resets them.

| Group | Setting | Choices (default first) |
|---|---|---|
| Units | Frequency axis of control plots | rad/s, Hz (Campbell diagrams are always in Hz) |
| Appearance | Theme | Light, Dark, System (follows Windows) |
| | Density | Normal, Compact, Roomy |
| | Text and plot size | 100 %, 90 %, 110 %, 125 % |
| | View list | Names, Icons (also ☰ or Ctrl+B) |
| Plots | Legend | Auto (inside, folded above the limit), Inside, Beside (column right of the plot), Above, Off |
| | Fold legends above | 6, 4, 10, never |
| | Plot height | Normal (1.3×), Compact, Tall |
| | Line width | Normal, Thin, Bold |
| | Grid | On, Fine (minor lines on linear axes too), Off |
| | Crosshair | Full (both lines, values on the axes and per curve, shared by related plots), Vertical, Off |
| | Plot tools | On hover, Always (in the plot's title row) |
| Accessibility | Curve colours | MATLAB, Colour-blind safe (Okabe–Ito), Vivid |
| | Contrast | Normal, High |
| | Motion | Normal, Reduced (no animations) |

Results are cached: returning to a tab, operating point or gain map is instant.

## 5. Open-loop tabs

| Tab | What it shows | Look for |
|---|---|---|
| **Operating points** | trim vs wind speed; rotor-speed deviation across the azimuth files; per-file debugging for the selected point; table with Ω spread, periodicity, azimuth-model Δf/Δζ | smooth trim; flat lines at 0 in the deviation plot; small periodicity after MBC |
| **Campbell diagram** | mode frequencies tracked across wind speeds (MAC), symmetry by line style, 1P/3P/6P/9P; damping vs wind. **Loop: open / closed** switches to the modes with the controller in the loop (open-loop tracks stay in grey; controller modes are added) | crossings with nP; damping near zero; modes the controller shifts or un-damps |
| **Modes** | eigenvalues in the s-plane; mode table; participation bars per mode | unstable or lightly damped modes and what they are |
| **Transfer function** | Bode + pole-zero map for any input → output; DC gain, peak, bandwidth, RHP zeros, cancelled poles | non-minimum-phase zeros (bandwidth limit) |
| **Time response** | exact step/impulse of the linear model; overshoot, rise, settling | pan right to extend the response |
| **Across wind speeds** | the same TF at every operating point; DC gain and resonance vs wind | how much the plant changes over the schedule |
| **Controllability & Hankel** | Kalman ranks, uncontrollable/unobservable modes, PBH plane, modal Hankel s-plane, Hankel singular values, all vs wind speed | modes a controller on these channels cannot affect; order needed for a reduced model |

Blade and whirl modes need blade DOFs in the linearisation; a banner says when there are none.

## 6. Closed-loop tabs

The ROSCO controller from DISCON is closed around each linearisation. Region 3 (pitch PI) at rated
rotor speed, else Region 2 (torque PI); override per point in the Stability summary table.

| Tab | What it shows |
|---|---|
| **Stability summary** | stable/unstable, GM, PM, s_m, least-damped mode vs wind; full table incl. disk margin, delay margin and Nyquist P → Z; **Loop delay** input; **Pitch–torque interaction** card (§6.1) |
| **Loop shape** | \|L\|, ∠L with every crossover; \|S\|, \|T\| |
| **Nyquist** | L(jω) with the s_m circle; **Near −1** / **Fit all** |
| **Poles & root locus** | closed- over open-loop poles with labels; root locus in loop gain k |
| **Gain study** | see below |
| **Time response** | closed vs open loop for a step/impulse on any plant input; disturbance rejection |
| **Controller & design** | gain schedule (DISCON and YAML); controller Bode; 1-DOF A, B; ω_n and ζ: target vs implied vs achieved |

Common guides: GM ≥ 6 dB, PM ≥ 30–45°, s_m ≥ 0.4–0.5, ‖S‖∞ ≤ 6–8 dB, disk margin about ±6 dB / ±37°.

### 6.1 Robustness extras (Stability summary)

* **Disk margin** — "±5.5 dB and ±34°" means the loop stays stable when the gain is off by up to
  5.5 dB *and* the phase by up to 34° at the same time. Gain and phase margins each assume only one of
  them is off, so the disk margin is smaller and closer to reality.
* **Loop delay [ms]** — the controller runs once per time step, which acts like a delay. Default
  1.5 × DT when the `.fst` is given; otherwise type it (e.g. 37.5 for DT = 25 ms); empty = off. The
  charts get a dashed "with delay" curve, the table a *PM with delay* column, and the header says
  whether every point is still stable.
* **Pitch–torque interaction** — both PI loops closed on generator speed at once (as around rated),
  compared with each loop alone: phase margins of each loop with and without the other, the coupling
  in dB, and whether the combination is stable. A note explains the pole at the origin that two
  integrators on one measurement always leave (ROSCO's set-point smoother handles it).
* **Region → Both loops (R2½)** in the table closes both loops at that wind speed for *every*
  closed-loop tab (loop shape, Nyquist, poles, gain study, time response).

### 6.2 Report

**Report** (page icon in the top bar) builds one self-contained HTML file: summary verdict, inputs, warnings, operating
points, open- and closed-loop Campbell diagrams, stability table with margins (and delay if set),
gain headroom, pitch–torque interaction, scope. It opens in a new tab and is downloaded; use
**Print / save as PDF** there. If the browser blocks the new tab, the downloaded file is the same.

### 6.3 Gain study

1. **Stability map** at the selected wind speed: each cell = one closed loop with Kp × x and Ki × y
   (factors on DISCON; ◆ = DISCON, ■ = YAML tuning). Colour = damping of the **speed-loop pole**
   (the pole ROSCO places) or of the least-damped pole the gains can move; red = unstable.
   Thick line = stability boundary, dashed = your ζ contour. Modes the gains cannot move are listed.
2. **Click a cell** (or type factors, **Analyse**) to compare with DISCON: margins, s_m, ‖S‖∞,
   speed-loop ω_n/ζ, poles, Nyquist near −1, step of generator speed and pitch to a 1 m/s wind step.
   Values outside the guides are red.
3. **PI gain scaling across wind speeds:** both PI gains × k everywhere; lines = largest and smallest
   stable k; table = gain headroom in dB. Click a cell to open it above.

Options: Kp × Ki or PI × floating (when Fl_Mode is on); range ×0.5–2 / ×0.25–4 / ×0.1–10;
resolution 15² / 25² / 41²; colour by speed-loop or least-damped. Factors apply at this operating
point: to use them, multiply `PC_GS_KP/KI` near this pitch in DISCON and re-run.

## 7. Workflows

### 7.1 Is the turbine stable, and with what margin?
Stability summary (any ✕? worst GM/PM/s_m and where) → Poles at that wind speed (which mode) →
Gain study scaling (headroom; too much or too little gain?) → Controllability & Hankel if a structural
mode is the problem (the PI gains cannot fix a mode the loop cannot see or excite).

### 7.2 Retuning the pitch loop at one wind speed
Gain study, colour by speed-loop damping, contour at the target ζ → click cells along it, compare
margins and pitch effort → check neighbours with ← → → transfer to DISCON and re-run.

### 7.3 Same wind speed, azimuths at different rotor speeds
LinBench keeps them as one operating point and classifies the difference:

| Pattern | Meaning | Action |
|---|---|---|
| **drift** (Ω follows simulation time) | rotor still accelerating; trim not converged | OpenFAST: `CalcSteady = True`, tighter `TrimTol`, or linearise later |
| **periodic** (Ω follows azimuth) | tower-shadow/shear ripple; expected | none; MBC uses each file's Ω |
| **scatter** (no pattern) | files from different runs/settings | check where the files came from |

Where to look:
1. Setup table: rotor speed with ±spread (red = warning). Run log: red WARNING line.
2. Operating points → **Rotor speed across the azimuth files**: each file's rotor speed as a deviation
   (%) from the mean at its wind speed; the files of one wind speed are spread sideways in azimuth
   order (0° left → 360° right) and joined. Flat line at 0 = clean; slope = drift; wave = 1P/3P ripple.
   Dashed lines at ±0.25 % = warning level for drift/scatter (0.5 % peak to peak); red = warning,
   amber = notice; shaded strip = selected wind speed.
3. **Rotor speed … in the order it was linearised**: Ω vs simulation time, labelled by azimuth. A straight
   rising/falling line means the rotor was still accelerating (trim not converged).
4. **Azimuth files** table: azimuth, time, Ω, ΔΩ, pitch, torque, generator speed per file. If pitch and
   torque also move, the controller was still acting during the sweep.
5. **Does it matter?** Each file is also MBC-transformed on its own: the **Δf / Δζ** columns and the
   *Mode frequency in each single-azimuth model* chart show how far each mode moves if you trusted that
   one file instead of the average. Whirl modes react most (their frequencies shift with ±Ω).

### 7.4 Checking a new linearisation set
Run page: periodicity after MBC small → Operating points: smooth trim, no Ω spread → Campbell:
crossings with 1P/3P, damping near zero.

## 8. Limits of the linear model

Small perturbations only (no saturation, rate limits, set-point smoothing, peak shaving, wind
estimator); continuous-time controller (its time step only as the optional loop delay, §6.1); azimuth
averaging assumes a near-isotropic rotor; treat points near rated with care.

## 9. Troubleshooting

| Symptom | Check |
|---|---|
| "No OpenFAST .lin files found" | folder; *include sub-folders* |
| Closed loop tab greyed out | give a DISCON.IN and choose Closed loop / Both |
| No blade or whirl modes | linearisation has no blade DOFs |
| "rotor speed drifts / differs … across azimuths" | §7.3 |
| Unstable at 1–2 points near rated | the region the loop was closed in; override it |
| Page does not show new features | close the console / old `LinBench.exe`, start again |
| Failure while loading | the Run page shows the failing step and a *Details* trace |

Validation results: `VALIDATION.md`; run `python tests/run_all.py` (set `ROSCO_DIR` for the ROSCO-based checks).
