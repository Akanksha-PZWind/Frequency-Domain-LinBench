"""Tuning parity: LinBench's re-implementation vs ROSCO's own tune_controller (gt/rosco_truth.json)."""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from linbench import rosco_io, tuning  # noqa: E402

sys.path.insert(0, HERE)
from _paths import TRUTH, TUNE  # noqa: E402


def run():
    truth = json.load(open(TRUTH))
    worst = {}
    for name, gt in truth.items():
        yp = os.path.join(TUNE, name)
        src = rosco_io.discover_from_yaml(yp)
        fast = rosco_io.read_fast_params(src['fst'])
        turb, ctrl, _ = tuning.turbine_from_sources(None, src['yaml'], fast)
        cpd = rosco_io.read_cp_table(src['cp'])
        cp = tuning.Surface(cpd['Cp'], cpd['pitch_rad'], cpd['tsr'])
        res = tuning.tune(cp, turb, ctrl)
        # TSR_op is excluded: ROSCO overwrites it in place afterwards (min-rotor-speed
        # saturation in min_pitch_saturation, controller.py ~L787); it does not feed the gains.
        for k in ('v', 'A', 'B_beta', 'pitch_op', 'pc_Kp', 'pc_Ki', 'PC_GS_angles', 'vs_Kp', 'vs_Ki'):
            a, b = np.asarray(res[k], float), np.asarray(gt[k], float)
            rel = float(np.max(np.abs(a - b)) / (np.max(np.abs(b)) or 1))
            worst[f'{name}:{k}'] = rel
            assert rel < 1e-9, (name, k, rel)
        assert abs(turb['J'] - gt['J']) < 1e-6
    return worst


if __name__ == '__main__':
    w = run()
    for k, v in w.items():
        print(f'{k:36s} max rel diff {v:.2e}')
    print('tuning parity OK')
