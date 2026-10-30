# LinBench — working rules for Claude

Local web app (Python engine + browser UI) for open/closed-loop analysis of OpenFAST
linearisations with a ROSCO controller. Read these before changing anything:

* `docs/ARCHITECTURE.md` — modules, data flow, API, front end, caches, tests, how to add things
* `docs/PHYSICS.md` — every equation, tolerance and threshold as implemented
* `docs/USER_GUIDE.md` (+ in-app `linbench/web/guide.html`) — what the user sees and does
* `docs/CHANGELOG.md` — history of changes

## Documentation is part of every change

Do not rewrite the documents; update only the parts a change touches, in the same turn as the code:

| Change | Update |
|---|---|
| any change | a new entry at the top of `docs/CHANGELOG.md` (what, why, files, tests) |
| new/renamed module, function responsibility, API endpoint, cache, panel, test file | `docs/ARCHITECTURE.md` |
| formula, tolerance, threshold, model assumption, controller block | `docs/PHYSICS.md` (and `MATH` texts in `app.js` if a Run-page step changes) |
| anything visible: screen, control, default, workflow, warning text | `docs/USER_GUIDE.md` **and** `linbench/web/guide.html` |
| start-up, inputs, folder layout | `README.md` |
| new validation check | register it in `tests/run_all.py`; record its numbers in `VALIDATION.md` |

## Tests

* Fast checks without ROSCO (run what the change touches; all take < 2 min):
  `python tests/test_discover.py`, `python tests/test_structure.py`, `python tests/test_modal.py`,
  `python tests/test_gains.py`, `python tests/test_robust.py`, and `python -c "import sys; sys.path[:0]=['.','tests']; import test_engine; print(test_engine.check_whirl())"`.
* **`python tests/run_all.py` overwrites `VALIDATION.md`.** Without `ROSCO_DIR` the ROSCO sections are
  written as "skipped" and the recorded IEA-15MW results are lost. Only run it when `ROSCO_DIR` is set;
  otherwise run the individual files and add their numbers to `VALIDATION.md` by hand.
* Sample generators: `tests/make_synthetic.py`, `tests/make_synthetic_discon.py`, `tests/make_drift_example.py`.

## Running the app while developing

* `python -m linbench --no-browser --port 8790` (the user may have `LinBench.exe` on 8765).
  `.claude/launch.json` holds this as the `linbench` preview configuration.
* Test UI changes in the browser pane on `samples/synthetic_rotor` (it has `synthetic_DISCON.IN`, so
  closed-loop tabs work); check the console for errors.
* The user restarts LinBench (or rebuilds with `build.ps1`) to see changes.
* Project layout mirrors TuneMe: `main.py`, `LinBench.spec`, `build.ps1`, one pinned `requirements.txt`,
  `tools/`, `assets/` (generated). No separate launchers; `.venv-build` holds the build environment.

## Conventions

* Operating points come from `.lin` header values, never file names.
* Matrices stay in OpenFAST units; display conversion only via `model.unit_convert`.
* ROSCO gains are negative; L(s) = −K·G; unstable means Re λ > 1e-7.
* Everything sent to the browser goes through `analysis.clean()`.
* Frequency/time curves shown in the browser should carry a `lti.modal_export` so they can be
  panned; the export is verified server-side and may be `null` (keep sampled arrays as fallback).
* The disk cache (`linbench/cache.py`, `~/.linbench/cache`) is used only when `cfg['cache']` is set
  (the server sets it; tests and scripts get fresh results). Its key includes a code stamp, so engine
  edits invalidate it automatically; bump `cache.FORMAT` if stored objects change shape otherwise.
* New features are pitched to the user and built only after they agree (they want the tool kept simple).
* Look and behaviour follow TuneMe (`..\TuneMe`, the user's other tool): light look by default,
  plot tools on hover + Enlarge, status bar, own Qt WebEngine window, offline guard, self-contained
  build. Since 2026-10-14 LinBench has its own shell: minimal top bar (icons), view list on the left,
  Settings dialog (units, dark theme, density, plots, accessibility), Ctrl+K palette. New colours must be
  CSS tokens with a value in the `data-theme=dark` block; new views need an icon in `IC`.
  `linbench/web/chart.js` is shared with TuneMe — keep both copies in step (LinBench's plot preferences
  are additions TuneMe does not have yet). Read TuneMe's folder only when the user asks; never change it.
* Never access the user's files outside this project without asking; anything Claude writes outside the
  project (e.g. `~/.linbench`) must be reported to the user.
* `build.ps1` downloads packages (PySide6 + WebEngine, ~300 MB) into `.venv-build`: run it only with the
  user's explicit permission.
* Environment: Windows, PowerShell, Python 3.14; `git` and `node` are not installed on this PC.
