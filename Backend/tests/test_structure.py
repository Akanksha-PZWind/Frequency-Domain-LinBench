"""Controllability, observability and Hankel singular values.

1. Random systems with a known Kalman structure (hidden by a random similarity): staircase
   ranks, minimal order and the uncontrollable / unobservable eigenvalues are recovered.
2. Stable/unstable split: HSVs of a system with unstable modes equal those of its known
   stable part.
3. Synthetic stable rotor: HSVs equal the brute-force Gramian result, and the number of
   significant HSVs equals the minimal order.
4. Synthetic isotropic rotor: collective pitch cannot excite the cyclic (whirl) modes, so
   exactly the FW/BW whirl modes (and tower side-side) are uncontrollable; adding the cyclic
   pitch inputs makes the model controllable.
"""
import os
import sys

import numpy as np
from scipy import linalg

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
from linbench import structure as ST  # noqa: E402
from linbench.discover import scan_folder  # noqa: E402
from linbench.modal import analyse_modes  # noqa: E402
from linbench.model import OpModel  # noqa: E402
from _paths import SYN  # noqa: E402


def _eig_err(a, b):
    from scipy.optimize import linear_sum_assignment
    if len(a) != len(b):
        return np.inf
    if not len(a):
        return 0.0
    D = np.abs(np.asarray(a)[:, None] - np.asarray(b)[None, :])
    r, c = linear_sum_assignment(D)
    return float(np.max(D[r, c] / np.maximum(1.0, np.abs(np.asarray(a)[r]))))


def _stable_block(rng, n):
    """Random real block with eigenvalues -0.1..-2 (complex pairs and reals)."""
    A = np.zeros((n, n))
    k = 0
    while k < n:
        if k + 1 < n and rng.random() < 0.6:
            s, w = -rng.uniform(0.05, 1.0), rng.uniform(0.5, 6.0)
            A[k:k + 2, k:k + 2] = [[s, w], [-w, s]]
            k += 2
        else:
            A[k, k] = -rng.uniform(0.1, 2.0)
            k += 1
    return A


def check_kalman():
    out = {}
    rng = np.random.default_rng(3)
    worst = 0.0
    for trial in range(20):
        nco, nc_, n_o, nx = 5, 3, 4, 2        # co: ctrb+obsv, c_: ctrb only, _o: obsv only, x: neither
        blocks = [_stable_block(rng, k) for k in (nco, nc_, n_o, nx)]
        n = sum(b.shape[0] for b in blocks)
        A = linalg.block_diag(*blocks)
        i = np.cumsum([0, nco, nc_, n_o, nx])
        # Kalman canonical couplings (upper-triangular structure keeps the decomposition exact)
        A[i[0]:i[1], i[2]:i[3]] = rng.normal(size=(nco, n_o))
        A[i[1]:i[2], i[0]:i[1]] = rng.normal(size=(nc_, nco))
        A[i[1]:i[2], i[3]:i[4]] = rng.normal(size=(nc_, nx))
        A[i[3]:i[4], i[2]:i[3]] = rng.normal(size=(nx, n_o))
        m, p = 2, 2
        B = np.zeros((n, m)); B[:i[2]] = rng.normal(size=(i[2], m))
        C = np.zeros((p, n)); C[:, i[0]:i[1]] = rng.normal(size=(p, nco)); C[:, i[2]:i[3]] = rng.normal(size=(p, n_o))
        T = rng.normal(size=(n, n))
        Ti = np.linalg.inv(T)
        At, Bt, Ct = Ti @ A @ T, Ti @ B, C @ T
        k = ST.kalman(At, Bt, Ct)
        assert (k['rank_c'], k['rank_o'], k['n_min']) == (nco + nc_, nco + n_o, nco), (trial, k['rank_c'], k['rank_o'], k['n_min'])
        unc = np.concatenate([linalg.eigvals(blocks[2]), linalg.eigvals(blocks[3])])
        uno = np.concatenate([linalg.eigvals(blocks[1]), linalg.eigvals(blocks[3])])
        worst = max(worst, _eig_err(k['uncontrollable'], unc), _eig_err(k['unobservable'], uno))
    assert worst < 1e-6, worst
    out['random Kalman structures (20): ranks and minimal order recovered'] = 1.0
    out['random Kalman structures: max rel error of uncontrollable/unobservable eigenvalues'] = worst
    # stable / unstable split
    worst = 0.0
    for trial in range(10):
        As = _stable_block(rng, 6)
        Au = np.array([[0.3, 1.0], [-1.0, 0.3]])
        A = linalg.block_diag(As, Au)
        B, C = rng.normal(size=(8, 2)), rng.normal(size=(2, 8))
        T = rng.normal(size=(8, 8)); Ti = np.linalg.inv(T)
        # the additive stable part of (A, B, C) is (As, B[:6], C[:, :6]) for block-diagonal A
        ref, _ = ST.hankel_sv(As, B[:6], C[:, :6])
        hsv, unst = ST.hankel_sv(Ti @ A @ T, Ti @ B, C @ T)
        assert len(unst) == 2, len(unst)
        worst = max(worst, float(np.max(np.abs(hsv - ref)) / ref[0]))
    assert worst < 1e-8, worst
    out['stable/unstable split: HSV rel error vs known stable part'] = worst
    return out


def check_rotor():
    out = {}
    info, ops = scan_folder(SYN)
    worst_hsv, worst_min = 0.0, 0
    for g in ops:
        om = OpModel(g.files)
        ms = analyse_modes(om)
        si, so = om.std_inputs(), om.std_outputs()
        # 3. brute-force Gramians on the full (stable) model
        assert np.all(np.linalg.eigvals(om.A).real < 0)
        ins, outs = [si['pitch'], si['torque']], [so['genspeed'], so['nacacc_fa']]
        r = ST.analyse(om, ms, ins, outs)
        B, C, _ = ST._scaled(om, ins, outs)
        Wc = linalg.solve_continuous_lyapunov(om.A, -B @ B.T)
        Wo = linalg.solve_continuous_lyapunov(om.A.T, -C.T @ C)
        ref = np.sort(np.sqrt(np.abs(np.linalg.eigvals(Wc @ Wo))))[::-1]
        hsv = np.asarray(r['hsv'])
        sig = int((hsv > 1e-8 * hsv[0]).sum())
        worst_hsv = max(worst_hsv, float(np.max(np.abs(hsv[:sig] - ref[:sig]) / hsv[:sig])))
        worst_min = max(worst_min, abs(sig - r['n_min']))
        # 4. collective pitch alone: whirl modes uncontrollable
        rc = ST.analyse(om, ms, [si['pitch']], [so['genspeed']])
        labs = sorted(u['label'] for u in rc['uncontrollable'])
        assert all(('whirl' in l) or l.startswith('Tower SS') for l in labs), labs
        assert sum('FW whirl' in l for l in labs) == 2 and sum('BW whirl' in l for l in labs) == 2, labs
        cyc = [i for i, rl in enumerate(om.urole) if rl in ('cos', 'sin')]
        rf = ST.analyse(om, ms, [si['pitch']] + cyc + [si['torque']], [so['genspeed']])
        assert rf['rank_c'] == rf['n'], rf['rank_c']
    assert worst_hsv < 1e-6, worst_hsv
    assert worst_min == 0, worst_min
    out['synthetic: HSV rel error vs brute-force Gramians'] = worst_hsv
    out['synthetic: |significant HSVs - minimal order|'] = float(worst_min)
    out['synthetic: collective pitch leaves exactly the whirl (+ tower SS) modes uncontrollable'] = 1.0
    return out


if __name__ == '__main__':
    for fn in (check_kalman, check_rotor):
        for k, v in fn().items():
            print(f'{k:80s} {v:.3e}')
    print('Structure validation OK')
