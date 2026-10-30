# LinBench

Open- and closed-loop analysis of OpenFAST linearisations with a ROSCO controller, as a self-contained Windows app in its own window (same design language as TuneMe).

## Start

| | |
|---|---|
| Any Windows 10/11 PC | unzip `LinBench-<version>-win64.zip` anywhere (a folder, a USB stick) and double-click `LinBench\LinBench.exe` — nothing to install, no internet; to remove it, delete the folder |
| From source | `python -m linbench` (options: `--port 8765`, `--browser`, `--no-browser`) |

`LinBench.exe` opens maximised in its own window (Qt WebEngine, bundled). Closing the window stops it; starting it again brings the open window to the front; `--browser` opens it in the default browser instead, and LinBench does that by itself if its window cannot start on a PC. It keeps to the PC: the window talks to it over 127.0.0.1 and any other connection is refused. Errors go to `%USERPROFILE%\.linbench\linbench_error.log`.

The first start on another PC may show *Windows protected your PC* (SmartScreen), because `LinBench.exe` is not signed with a purchased certificate: **More info → Run anyway**. The very first start after unzipping takes longer while Windows scans the new files.

From source, a Python with numpy, scipy and pyyaml is enough; without PySide6 the page opens in the browser and the console window is the server.

## Building the exe

`powershell -ExecutionPolicy Bypass -File build.ps1` needs Windows and [uv](https://docs.astral.sh/uv/), and the internet once to fetch the pinned packages (`requirements.txt`, incl. PySide6 with Qt WebEngine) into `.venv-build`. It writes `dist\LinBench\` — `LinBench.exe`, its libraries (of Qt only what the window loads), `docs`, `samples` and the complete `source` — checks that the folder needs nothing installed (`tools/audit_bundle.py`), and packs `dist\LinBench-<version>-win64.zip`.

## Inputs

| Input | Needed for | Notes |
|---|---|---|
| Linearisation folder | everything | any `.lin` names; order is read from the files |
| `DISCON.IN` | closed loop | gains, filters, floating feedback, pitch actuator |
| ROSCO tuning YAML | optional | fills the other paths (`path_params`, `linmodel_tuning.linfile_path`) and gives ω_pc / ζ_pc targets |
| Cp-Ct-Cq table, `.fst` | optional | found automatically from the DISCON or YAML when possible |

Choose **Open loop**, **Closed loop** or **Both**, then **Run analysis**. A DISCON in the linearisation folder (or one level up) is picked up automatically. While it runs, the page shows each step with its maths, live charts (periodicity before/after MBC, modes per wind speed, closed-loop margins) and a log of every result. **⋯ → Analysis log** in the top bar brings it back later.

The full **usage guide and maths reference** is in the app: **⋯ → Guide**, or `linbench/web/guide.html`.

## Plots

Drag any plot to pan it (mouse or touch); Ctrl/⌘ + wheel, a trackpad pinch or **+ / −** zooms. **Shift+drag** (or **Box zoom**) draws a zoom box: a flat box zooms time/frequency only, a tall box the vertical axis only. Double-click or **Reset** to go back. Bode, loop-shape, controller and time-response curves are recomputed for the visible range, so they continue when you pan or zoom out. Click a legend entry to hide a curve, double-click it to show only that curve, point at it to highlight it; plots with many curves fold their legend to "N curves". **PNG / SVG / CSV** export each plot.

**Open loop** — Operating points · Campbell diagram (collective / backward / forward whirl) · Modes · Transfer function (any input → any output) · Time response · Across wind speeds · Controllability & Hankel (Kalman ranks, uncontrollable / unobservable modes, PBH measures per mode, Hankel singular values with the balanced-truncation bound, all across wind speeds)

**Closed loop** — Stability summary · Loop shape (L, S, T) · Nyquist · Poles & root locus · Gain study (Kp × Ki stability map, click any cell to compare margins, poles, Nyquist and step responses with DISCON; PI gain headroom at every wind speed) · Time response (closed vs open) · Controller & design

The Stability summary also gives the disk margin (gain and phase together), margins with a loop delay (controller time step) and the pitch–torque interaction around rated. The Campbell diagram has an open / closed loop switch. **Report** (page icon in the top bar) writes the whole analysis to one HTML file that prints to PDF.

Views are listed on the left; **Ctrl+K** finds any view, wind speed or action; ← / → step through operating points. **Settings** (gear, Ctrl+,): rad/s or Hz, light / dark theme, density, text size, legend style, line width, grid, crosshair, colour-blind palette, high contrast, reduced motion. Results are cached, so returning to a tab or operating point is instant. A folder analysed before reopens at once: processed results are kept in `~/.linbench/cache` and reused while no `.lin` file changes.

## Folder

```
linbench/          source (Python engine + web/ front end)
tests/             validation (python tests/run_all.py; set ROSCO_DIR for the ROSCO-based checks)
samples/           synthetic 3-bladed rotor with blade DOFs (shows collective / whirl modes) and
                   synthetic_DISCON.IN, a ROSCO-style controller for it (tests/make_synthetic_discon.py);
                   rotor_speed_drift/: 17 m/s linearised while the rotor was still accelerating
                   (demonstrates the rotor-speed diagnostics; tests/make_drift_example.py)
docs/              ARCHITECTURE.md (code structure) · PHYSICS.md (equations) · USER_GUIDE.md · CHANGELOG.md
tools/             build helpers (icon, version info, self-contained check)
main.py, LinBench.spec, build.ps1, requirements.txt   entry point and build
VALIDATION.md      latest validation results
CLAUDE.md          working rules for Claude sessions (which document to update with each change)
```

## Documentation

| Document | For |
|---|---|
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | using the app (also in the app: **Guide**) |
| [docs/PHYSICS.md](docs/PHYSICS.md) | what is computed: MBC, modes, margins, controller model, gain study, all equations |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | how the code is organised; API; how to add a feature |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | what changed and when |
