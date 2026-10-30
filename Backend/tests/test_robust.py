"""Delay, disk margin, both loops closed, closed-loop mode tracking, disk cache.
Synthetic rotor + samples/synthetic_rotor/synthetic_DISCON.IN (no ROSCO needed).

1. Delay: at every gain crossover the phase margin drops by exactly w_c * tau; the Nyquist verdict
   flips from stable to unstable between 0.95 and 1.05 times the analytic delay margin.
2. Disk margin: alpha = 1/max|S - 1/2| on a dense grid; it never exceeds the classical margins; the
   loop is still stable (eigenvalues) at 98 % of the disk gain limits in both directions.
3. Pitch and torque PI closed together: the closed-loop poles do not depend on where the loop is
   broken, and there is exactly one pole at the origin (two integrators on one measurement).
4. Closed-loop Campbell: with the controller gains scaled to ~0 the plant-dominated closed-loop
   modes are the open-loop modes; with the real gains every structural track is found at all points.
5. Disk cache: a cached reload gives bit-identical matrices and the same summary.
"""
import math
import os
import sys

import numpy as np
from scipy import linalg

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
from linbench import cache  # noqa: E402
from linbench import closedloop as CL  # noqa: E402
from linbench import gainstudy as GS  # noqa: E402
from linbench.analysis import Session  # noqa: E402
from linbench.modal import analyse_modes  # noqa: E402
from _paths import SYN  # noqa: E402

DISCON = os.path.join(SYN, 'synthetic_DISCON.IN')


def _session(**kw):
    s = Session()
    s._load(dict(lin_dir=SYN, discon=DISCON, mode='both', **kw))
    assert not s.progress.get('error'), s.progress.get('error')
    return s


def run():
    out = {}
    s = _session()
    e_pm = e_alpha = 0.0
    n_flip = n_disk = 0
    for i, om in enumerate(s.models):
        cm = s.cm(i)
        r0 = CL.loop_analysis(cm)
        # 1. delay
        tau = 0.3
        rd = CL.loop_analysis(cm, tau=tau)
        for a, b in zip(sorted(r0['pm'], key=lambda p: p['w']), sorted(rd['pm'], key=lambda p: p['w'])):
            assert abs(a['w'] - b['w']) < 1e-6 * a['w']
            e_pm = max(e_pm, abs((a['pm'] - math.degrees(a['w'] * tau)) - b['pm']))
        dm = r0['delay_margin']
        if dm and dm > 0:
            assert CL.loop_analysis(cm, tau=0.95 * dm)['stable'], (om.wind, dm)
            assert not CL.loop_analysis(cm, tau=1.05 * dm)['stable'], (om.wind, dm)
            n_flip += 1
        # 2. disk margin
        w = np.unique(np.concatenate([np.logspace(-4, 2, 20000), r0['w']]))
        S = 1 / (1 + cm.loop_eval(1j * w))
        alpha = 1 / np.abs(S - 0.5).max()
        e_alpha = max(e_alpha, abs(alpha - r0['disk_alpha']) / alpha)
        if r0['gm_min']:
            assert r0['disk_gm_db'] <= abs(r0['gm_min']['gm_db']) + 1e-6
        if r0['pm_min']:
            assert r0['disk_pm'] <= abs(r0['pm_min']['pm']) + 1e-6
        g = (2 + r0['disk_alpha']) / (2 - r0['disk_alpha'])
        for k in (1 + 0.98 * (g - 1), 1 / (1 + 0.98 * (g - 1))):
            assert linalg.eigvals(cm.closed_loop(k)[0]).real.max() <= 1e-7, (om.wind, k)
            n_disk += 1
    assert e_pm < 1e-6, e_pm
    assert e_alpha < 2e-3, e_alpha
    out['delay: max |PM(tau) - (PM - w_c tau)| at gain crossovers [deg]'] = e_pm
    out['delay: points where stability flips at the analytic delay margin (+-5 %)'] = float(n_flip)
    out['disk margin: rel error of alpha vs dense-grid 1/max|S - 1/2|'] = e_alpha
    out['disk margin: gain perturbations at 98 % of the disk limit, all stable'] = float(n_disk)
    # 3. both loops
    e_same = 0.0
    n0 = set()
    for i, om in enumerate(s.models):
        a = np.sort_complex(linalg.eigvals(CL.ControllerModel(om, s.discon, region='both').closed_loop()[0]))
        b = np.sort_complex(linalg.eigvals(CL.ControllerModel(om, s.discon, region='both_tq').closed_loop()[0]))
        e_same = max(e_same, float(np.max(np.abs(np.sort(np.abs(a)) - np.sort(np.abs(b))))))
        n0.add(int(np.sum(np.abs(a) < 1e-6)))
    assert e_same < 1e-8, e_same
    assert n0 == {1}, n0
    out['both loops: closed-loop poles independent of where the loop is broken (max diff)'] = e_same
    out['both loops: poles at the origin (two integrators on one measurement)'] = 1.0
    # 4. closed-loop modes
    e_f = 0.0
    for i, om in enumerate(s.models):
        ol = [m for m in analyse_modes(om) if m['oscillatory']]
        cl = [m for m in CL.cl_modes(GS.with_gains(s.cm(i), 1e-9, 1e-9)) if m['family'] != 'controller']
        for m in ol:
            j = min(cl, key=lambda c: abs(c['fn'] - m['fn']) + abs(c['zeta'] - m['zeta']))
            e_f = max(e_f, abs(j['fn'] - m['fn']) / m['fn'] + abs(j['zeta'] - m['zeta']))
            assert j['label'] == m['label'], (om.wind, j['label'], m['label'])
    assert e_f < 1e-6, e_f
    c = s.campbell_data(closed=True)
    struct = [t for t in c['tracks'] if t['family'] != 'controller']
    assert len(struct) == 8 and all(len(t['points']) == len(s.models) for t in struct), [(t['label'], len(t['points'])) for t in struct]
    out['closed-loop modes at ~zero gain vs open-loop modes (frequency + damping, labels equal)'] = e_f
    out['closed-loop Campbell: structural tracks complete over all wind speeds'] = float(len(struct))
    # 5. disk cache (isolated folder so the user's cache is untouched)
    import tempfile
    old = cache.DIR
    cache.DIR = tempfile.mkdtemp()
    try:
        a = _session(cache=True)
        b = _session(cache=True)
        assert any(l['data'].get('cached') for l in b.progress['log'] if l['stage'] == 'scan')
        d = max(float(np.max(np.abs(x.A - y.A))) + float(np.max(np.abs(x.B - y.B))) for x, y in zip(a.models, b.models))
        assert d == 0.0, d
        assert [r['gm_db'] for r in a.cl_summary] == [r['gm_db'] for r in b.cl_summary]
        assert a.summary['campbell'] == b.summary['campbell']
        out['disk cache: reload vs fresh, max |A diff| + |B diff|'] = d
    finally:
        import shutil
        shutil.rmtree(cache.DIR, ignore_errors=True)
        cache.DIR = old
    return out


if __name__ == '__main__':
    for k, v in run().items():
        print(f'{k:88s} {v:.3e}')
    print('Robustness / interaction / cache validation OK')
