"""Gain study (synthetic rotor + samples/synthetic_rotor/synthetic_DISCON.IN, no ROSCO needed).

1. Factors 1 x 1 reproduce the DISCON closed loop exactly.
2. The upper limit of the common PI factor equals the gain margin from the Nyquist/loop analysis
   (no floating feedback, so scaling both PI gains scales the whole loop).
3. The stable / unstable verdict of the map agrees with the eigenvalue + Nyquist analysis
   (Z = N + P) of the rebuilt controller at scattered gain settings.
4. The speed-loop pole picked by participation equals the one reported by the design comparison.
"""
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
from linbench import closedloop as CL  # noqa: E402
from linbench import gainstudy as GS  # noqa: E402
from linbench.analysis import Session  # noqa: E402
from _paths import SYN  # noqa: E402

DISCON = os.path.join(SYN, 'synthetic_DISCON.IN')


def run():
    s = Session()
    s._load(dict(lin_dir=SYN, discon=DISCON, mode='both'))
    assert not s.progress.get('error'), s.progress.get('error')
    out = {}
    e_same = e_gm = e_sp = 0.0
    n_pts = n_agree = 0
    rng = np.random.default_rng(11)
    for i, om in enumerate(s.models):
        cm = s.cm(i)
        A0 = cm.closed_loop()[0]
        A1 = GS.with_gains(cm, 1.0, 1.0).closed_loop()[0]
        e_same = max(e_same, float(np.max(np.abs(A1 - A0))))
        row = s.cl_summary[i]
        k_lo, k_hi = GS.stable_range(cm)
        if row['gm_db'] is not None:
            gm = 10 ** (row['gm_db'] / 20)
            assert k_hi is not None, (om.wind, gm)
            e_gm = max(e_gm, abs(k_hi - gm) / gm)
        for _ in range(6):
            fx, fy = np.exp(rng.uniform(math.log(0.2), math.log(5.0), 2))
            c = GS.with_gains(cm, fx, fy)
            m = GS.Metric(c)(c.closed_loop()[0])
            r = CL.loop_analysis(c)
            n_pts += 1
            assert r['nyquist_ok'], (om.wind, fx, fy)
            n_agree += int(m['stable'] == r['stable'])
        d = s.summary['design'][i]
        if d.get('omega_achieved') is not None:
            A, _, _, _, states = cm.closed_loop()
            m = GS.Metric(cm)(A, states)
            e_sp = max(e_sp, abs(m['wn_sp'] - d['omega_achieved']) / d['omega_achieved'])
    assert e_same == 0.0, e_same
    assert e_gm < 1e-6, e_gm
    assert n_agree == n_pts, (n_agree, n_pts)
    assert e_sp < 1e-9, e_sp
    out['factors 1 x 1 vs DISCON closed loop: max |A diff|'] = e_same
    out['upper PI-scaling limit vs gain margin, max rel diff'] = e_gm
    out['random gain settings: map verdict = eigenvalue/Nyquist verdict'] = float(n_agree) / n_pts
    out['random gain settings checked (all with Z = N + P)'] = float(n_pts)
    out['speed-loop pole vs design comparison, max rel diff'] = e_sp
    return out


if __name__ == '__main__':
    for k, v in run().items():
        print(f'{k:70s} {v:.3e}')
    print('Gain study validation OK')
