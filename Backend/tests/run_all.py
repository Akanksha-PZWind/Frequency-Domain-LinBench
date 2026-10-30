"""Run every validation check and write VALIDATION.md.   python tests/run_all.py"""
import datetime
import os
import platform
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))
import _paths  # noqa: E402

rows, ok_all = [], True


def section(title, fn, needs_rosco):
    global ok_all
    if needs_rosco and not _paths.R:
        rows.append((title, 'skipped', 'set ROSCO_DIR to a ROSCO checkout to run this check', {}))
        return
    try:
        res = fn()
        rows.append((title, 'pass', '', res))
    except Exception as e:
        ok_all = False
        rows.append((title, 'FAIL', f'{type(e).__name__}: {e}', {}))
        traceback.print_exc()


import test_discover  # noqa: E402
import test_engine  # noqa: E402
import test_gains  # noqa: E402
import test_mbc  # noqa: E402
import test_modal  # noqa: E402
import test_robust  # noqa: E402
import test_structure  # noqa: E402
import test_tuning  # noqa: E402

section('Rotor-mode symmetry and whirl (synthetic isotropic rotor)', test_engine.check_whirl, False)
section('Grouping when rotor speed differs between azimuths (drift / ripple / scatter / two sets)', test_discover.run, False)
section('Controllability, observability, Kalman decomposition (known structures)', test_structure.check_kalman, False)
section('Hankel singular values and PBH tests (synthetic rotor)', test_structure.check_rotor, False)
section('Browser-side curves: pole/residue exports, open loop (synthetic rotor)', test_modal.check_open, False)
section('Browser-side curves: loop and controller exports (synthetic rotor + synthetic DISCON)', test_modal.check_closed_synthetic, False)
section('Browser-side curves: loop and controller exports (IEA-15MW)', test_modal.check_closed, True)
section('Gain study: stability map, PI-scaling limits, speed-loop pole (synthetic DISCON)', test_gains.run, False)
section('Delay, disk margin, both loops closed, closed-loop Campbell, disk cache (synthetic DISCON)', test_robust.run, False)
section('MBC transform vs ROSCO fx_mbc3, periodicity, file-name invariance', test_mbc.run, True)
section('Transfer functions (modal residues, DC gain, zeros)', test_engine.check_tf, True)
section('ROSCO controller model, loop gain, closed-loop poles, Nyquist', test_engine.check_closed_loop, True)
section('Tuning re-implementation vs ROSCO tune_controller', test_tuning.run, True)

def _rosco_version():
    try:
        import re
        txt = open(os.path.join(_paths.R, 'pyproject.toml')).read()
        return 'ROSCO ' + re.search(r'^version\s*=\s*"([^"]+)"', txt, re.M).group(1)
    except Exception:
        return 'ROSCO checkout' if _paths.R else 'not available'


lines = [f'# LinBench validation', '', f'Run {datetime.datetime.now():%Y-%m-%d %H:%M} · Python {platform.python_version()} · '
         f'reference: {_rosco_version()}', '']
for title, status, note, res in rows:
    lines.append(f'## {title} — {status}')
    if note:
        lines.append(f'{note}')
    if res:
        lines += ['', '| check | value |', '|---|---|']
        lines += [f"| {k.replace('|', '‖')} | {v:.3e} |" for k, v in res.items()]
    lines.append('')
out = os.path.join(os.path.dirname(HERE), 'VALIDATION.md')
open(out, 'w', encoding='utf-8').write('\n'.join(lines))
print('\n'.join(lines))
sys.exit(0 if ok_all else 1)
