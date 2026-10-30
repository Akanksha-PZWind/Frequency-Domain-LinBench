"""MBC validation.

1. Eigenvalues of the MBC-averaged A match ROSCO's own fx_mbc3 (IEA-15MW floating case and
   the synthetic rotor with blade DOFs).
2. Synthetic isotropic rotor: after MBC the system is time-invariant (periodicity ~ 0).
3. IEA case: B column of the MBC collective of the three blade-pitch inputs equals the
   column of OpenFAST's own 'collective blade-pitch' extended input.
4. File-name invariance: shuffling / renaming the files changes nothing.
"""
import glob
import importlib.util as iu
import os
import shutil
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
from linbench.discover import scan_folder  # noqa: E402
from linbench.model import OpModel  # noqa: E402

sys.path.insert(0, HERE)
from _paths import IEA, SYN, R as ROSCO  # noqa: E402


def rosco_mbc():
    sys.path.insert(0, ROSCO)
    sp = iu.spec_from_file_location('mbc3', os.path.join(ROSCO, 'rosco/toolbox/linear/mbc3.py'))
    m = iu.module_from_spec(sp)
    sp.loader.exec_module(m)
    return m


def match_eigs(a, b):
    from scipy.optimize import linear_sum_assignment
    D = np.abs(a[:, None] - b[None, :])
    r, c = linear_sum_assignment(D)
    return float(np.max(D[r, c] / np.maximum(1e-3, np.abs(a[r]))))


def run():
    out = {}
    m = rosco_mbc()
    for name, folder in (('IEA15-semi', IEA), ('synthetic', SYN)):
        info, ops = scan_folder(folder)
        for k in (0, len(ops) // 2, len(ops) - 1):
            om = OpModel(ops[k].files, remove_azimuth=False)   # ROSCO fx_mbc3 keeps it
            import contextlib
            import io
            with contextlib.redirect_stdout(io.StringIO()):
                MBC, _, _ = m.fx_mbc3(ops[k].files, verbose=False)
            e1 = np.linalg.eigvals(om.A)
            e2 = np.linalg.eigvals(MBC['AvgA'])
            err = match_eigs(e1, e2)
            out[f'{name} U={om.wind:g} eig rel err vs ROSCO fx_mbc3'] = err
            assert err < 1e-6, err
    # periodicity of the synthetic rotor after MBC
    info, ops = scan_folder(SYN)
    for g in ops:
        om = OpModel(g.files)
        assert om.periodicity_mbc < 1e-5, om.periodicity_mbc
    out['synthetic max periodicity after MBC'] = max(OpModel(g.files).periodicity_mbc for g in ops)
    out['synthetic periodicity before MBC'] = OpModel(ops[-1].files).periodicity_raw
    # collective of blade pitch == extended collective input
    info, ops = scan_folder(IEA)
    om = OpModel(ops[12].files)
    i_ext = om.find_input('collective blade-pitch command')
    i_col = [t for t in om.plan.u_trip if 'pitch' in om.u[t[0]].desc.lower()][0][0]
    rel = np.linalg.norm(om.B[:, i_col] - om.B[:, i_ext]) / np.linalg.norm(om.B[:, i_ext])
    out['IEA collective(B pitch 1..3) vs extended input, rel diff'] = float(rel)
    assert rel < 5e-3, rel
    # renaming / shuffling invariance
    tmp = tempfile.mkdtemp()
    files = sorted(glob.glob(os.path.join(IEA, '*.lin')))
    rng = np.random.default_rng(7)
    perm = rng.permutation(len(files))
    for f, p in zip(files, perm):
        shutil.copy(f, os.path.join(tmp, f'case_{p:04d}.lin'))
    info2, ops2 = scan_folder(tmp)
    assert [round(g.wind_speed, 6) for g in ops2] == [round(g.wind_speed, 6) for g in ops]
    om2 = OpModel(ops2[12].files)
    out['renamed/shuffled set: max |A diff|'] = float(np.max(np.abs(om2.A - om.A)))
    assert np.allclose(om2.A, om.A)
    shutil.rmtree(tmp)
    return out


if __name__ == '__main__':
    for k, v in run().items():
        print(f'{k:60s} {v:.3e}')
    print('MBC validation OK')
