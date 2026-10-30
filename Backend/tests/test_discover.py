"""Grouping of azimuth files when rotor speed differs within one wind speed.

Synthetic rotor, 8 azimuths per wind speed, written in time order (as OpenFAST does):
  drift     Omega rises linearly with time by 0.5 / 2 / 5 %   -> one point, 'drift', warning
  periodic  Omega = mean * (1 + 0.4 % cos 3 psi)               -> one point, 'periodic', notice only
  scatter   Omega random +-1 %, not tied to time or azimuth    -> one point, 'scatter', warning
  two sets  two complete azimuth sweeps at 11.0 and 12.1 rpm   -> two points (azimuths repeat)
The reference files (bit-identical to samples/synthetic_rotor) must be regrouped unchanged, and
the single-azimuth models of a drifting set must disagree more than those of a clean set.
"""
import math
import os
import shutil
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
import make_synthetic as MS  # noqa: E402
from linbench.analysis import Session  # noqa: E402
from linbench.discover import scan_folder  # noqa: E402
from linbench.model import OpModel  # noqa: E402
from _paths import SYN  # noqa: E402

N = 8
PSI = [2 * math.pi * j / N + 0.013 for j in range(N)]


def _sweep(U, omegas, t0=100.0):
    return [(U, PSI[j], omegas[j], t0 + j * 0.6) for j in range(N)]


def _case(tmp, name, extra):
    """Clean 14 and 20 m/s around the wind speed under test (17 m/s)."""
    folder = os.path.join(tmp, name)
    W = MS.rated_speed
    MS.write_files(folder, _sweep(14.0, [W(14.0)] * N) + extra + _sweep(20.0, [W(20.0)] * N))
    return scan_folder(folder)


def run():
    out = {}
    tmp = tempfile.mkdtemp()
    try:
        W0 = MS.rated_speed(17.0)
        # --- drift
        for spread in (0.005, 0.02, 0.05):
            om = [W0 * (1 + spread * (j / (N - 1) - 0.5)) for j in range(N)]
            info, g = _case(tmp, f'drift{spread}', _sweep(17.0, om))
            g17 = [x for x in g if abs(x.wind_speed - 17) < 1e-6]
            assert len(g17) == 1 and len(g17[0].files) == N, [len(x.files) for x in g17]
            oc = g17[0].omega_check
            assert oc['kind'] == 'drift' and oc['level'] == 'warning', oc
            assert abs(oc['spread'] - spread) < 2e-4, oc     # header stores Omega to 1e-4 rad/s
            if spread == 0.05:
                out['drift 5 %: azimuth files kept in one operating point'] = float(len(g17[0].files))
                out['drift 5 %: adjusted R^2 time / azimuth'] = oc['r2_time']
                s = Session._az_sens(OpModel(g17[0].files))
                s0 = Session._az_sens(OpModel([x for x in g if abs(x.wind_speed - 14) < 1e-6][0].files))
                assert s['df'] > 1e3 * s0['df'], (s['df'], s0['df'])
                out['drift 5 %: max mode-frequency spread between azimuth models [%]'] = s['df'] * 100
                out['clean set: max mode-frequency spread between azimuth models [%]'] = s0['df'] * 100
        # --- periodic 3P ripple (converged periodic trim): recognised, no warning at 0.4 %
        om = [W0 * (1 + 0.004 * math.cos(3 * p)) for p in PSI]
        info, g = _case(tmp, 'periodic', _sweep(17.0, om))
        oc = [x for x in g if abs(x.wind_speed - 17) < 1e-6][0].omega_check
        assert oc['kind'] == 'periodic' and oc['level'] != 'warning', oc      # 0.8 % peak-to-peak: a notice
        out['periodic +-0.4 % ripple: classified periodic, notice only'] = 1.0
        # --- scatter
        rng = np.random.default_rng(5)
        om = list(W0 * (1 + 0.01 * rng.uniform(-1, 1, N)))
        info, g = _case(tmp, 'scatter', _sweep(17.0, om))
        g17 = [x for x in g if abs(x.wind_speed - 17) < 1e-6]
        assert len(g17) == 1, len(g17)
        assert g17[0].omega_check['kind'] == 'scatter' and g17[0].omega_check['level'] == 'warning', g17[0].omega_check
        out['random 1 % scatter: classified scatter, warning'] = 1.0
        # --- two genuine sets at one wind speed (repeated azimuths): kept apart by rotor speed
        W1 = 11.0 * math.pi / 30
        info, g = _case(tmp, 'twosets', _sweep(17.0, [W0] * N) + _sweep(17.0, [W1] * N, t0=300.0))
        g17 = [x for x in g if abs(x.wind_speed - 17) < 1e-6]
        assert len(g17) == 2 and all(len(x.files) == N for x in g17), [len(x.files) for x in g17]
        assert all(x.omega_check['level'] == 'ok' for x in g17)
        assert any('share wind speed 17' in w for w in info['warnings']), info['warnings']
        out['two full sets at 17 m/s (11.0 / 12.1 rpm): separate points'] = float(len(g17))
        # --- reference data unchanged
        info, g = scan_folder(SYN)
        assert len(g) == 8 and all(len(x.files) == 8 and x.omega_check['level'] == 'ok' for x in g)
        out['reference synthetic set: 8 points x 8 azimuths, no rotor-speed warning'] = 1.0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return out


if __name__ == '__main__':
    for k, v in run().items():
        print(f'{k:75s} {v:.3e}')
    print('Grouping / rotor-speed validation OK')
