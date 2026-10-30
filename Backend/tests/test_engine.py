"""Engine checks: whirl classification, transfer functions, controller model, closed loop, Nyquist."""
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
from linbench import closedloop as CL  # noqa: E402
from linbench import lti  # noqa: E402
from linbench.discover import scan_folder  # noqa: E402
from linbench.modal import analyse_modes, campbell  # noqa: E402
from linbench.model import OpModel  # noqa: E402
from linbench.rosco_io import read_discon  # noqa: E402
from linbench.tf import SISO  # noqa: E402

sys.path.insert(0, HERE)
from _paths import IEA, DISCON, SYN  # noqa: E402
import make_synthetic as MS  # noqa: E402


def check_whirl():
    """Synthetic isotropic rotor: the uncoupled cyclic flap pair must sit exactly at
    lambda_rot +/- j*Omega and be labelled BW (minus) / FW (plus); collective flap is 'collective'."""
    out = {}
    info, ops = scan_folder(SYN)
    worst = 0.0
    for g in ops:
        om = OpModel(g.files)
        ms = analyse_modes(om)
        Om = om.omega
        load = 0.5 + 0.5 * min(om.wind / 12.0, 1.0)
        w = 2 * math.pi * MS.P['f_flap']
        c = 2 * MS.P['z_flap'] * load * w
        lr = [r for r in np.roots([1, c, w * w]) if r.imag > 0][0]
        for target, sym in ((lr + 1j * Om, 'FW'), (lr - 1j * Om, 'BW')):
            m = min(ms, key=lambda m: abs(complex(m['re'], m['im']) - target))
            err = abs(complex(m['re'], m['im']) - target) / abs(target)
            worst = max(worst, err)
            assert err < 1e-6, (om.wind, sym, err)
            assert m['sym'] == sym and m['family'] == 'blade flap', (m['label'], sym)
        coll = [m for m in ms if m['family'] == 'blade flap' and m['sym'] == 'collective']
        assert coll, 'collective flap mode missing'
    out['synthetic: max error of FW/BW flap eigenvalues vs lambda_rot +/- j*Omega'] = worst
    cb = campbell([OpModel(g.files) for g in ops])
    labels = sorted(t['label'] for t in cb['tracks'])
    expect = ['Blade edge 1 · BW whirl', 'Blade edge 1 · FW whirl', 'Blade edge 1 · collective',
              'Blade flap 1 · BW whirl', 'Blade flap 1 · FW whirl', 'Blade flap 1 · collective', 'Tower FA 1', 'Tower SS 1']
    assert labels == sorted(expect), labels
    assert all(len(t['points']) == len(ops) for t in cb['tracks'])
    out['synthetic: Campbell tracks (complete over all wind speeds)'] = float(len(cb['tracks']))
    return out


def check_tf():
    out = {}
    info, ops = scan_folder(IEA)
    om = OpModel(ops[12].files)
    si, so = om.std_inputs(), om.std_outputs()
    s = SISO(om, si['pitch'], so['genspeed'])
    w = np.logspace(-3, 1.3, 60)
    Gm = s.response(w)
    Gd = lti.freqresp(om.A, om.B[:, [si['pitch']]], om.C[[so['genspeed']]], om.D[[so['genspeed']]][:, [si['pitch']]], w)[:, 0, 0] * s.scale
    e = float(np.max(np.abs(Gm - Gd) / np.abs(Gd)))
    out['IEA 17 m/s: modal-residue vs direct frequency response, max rel diff'] = e
    assert e < 1e-8
    r = s.analyse()
    # A has hydrodynamic integrator states (eigenvalue 0) that the input cannot reach: evaluate G
    # directly at a very low frequency instead of -C A^-1 B + D
    dc = float(lti.freqresp(om.A, om.B[:, [si['pitch']]], om.C[[so['genspeed']]], om.D[[so['genspeed']]][:, [si['pitch']]], [1e-9])[0, 0, 0].real * s.scale)
    out['IEA 17 m/s: DC gain vs direct G(j*1e-9), rel diff'] = abs(r['metrics']['dc_gain'] - dc) / abs(dc)
    assert abs(r['metrics']['dc_gain'] - dc) / abs(dc) < 1e-8
    # zeros of the minimal realisation are zeros of G
    z = [complex(q['re'], q['im']) for q in r['zeros']]
    Gz = max(abs(complex(np.sum(s.res / (zz - s.lam)) + s.d)) for zz in z if abs(zz) < 5) if z else 0
    out['IEA 17 m/s: max |G(z)| over reported zeros (scaled)'] = float(Gz / max(np.abs(Gm)) * s.scale)
    assert Gz / max(np.abs(Gm)) * s.scale < 1e-5
    return out


def check_closed_loop():
    out = {}
    dis = read_discon(DISCON)
    info, ops = scan_folder(IEA)
    worst_ctrl, worst_loop, worst_root = 0.0, 0.0, 0.0
    nyq_ok = True
    for g in ops:
        om = OpModel(g.files)
        cm = CL.ControllerModel(om, dis)
        s = 1j * np.logspace(-3, 1.3, 40)
        K = cm.ctrl_eval(s)
        # analytic controller transfer functions (ROSCO continuous prototypes)
        wc, zc = dis['F_LPFCornerFreq'], dis['F_LPFDamping']
        lpf = wc ** 2 / (s ** 2 + 2 * zc * wc * s + wc ** 2)
        wa, za = dis['PA_CornerFreq'], dis['PA_Damping']
        pa = wa ** 2 / (s ** 2 + 2 * za * wa * s + wa ** 2)
        if cm.region == 'pitch':
            Ks = -(cm.Kp + cm.Ki / s) * lpf * pa
            fw, fz = dis['F_FlCornerFreq']
            hp = dis['F_FlHighPassFreq']
            wn, bn, bd = dis['F_NotchFreqs'], dis['F_NotchBetaNum'], dis['F_NotchBetaDen']
            notch = (s ** 2 + 2 * bn * wn * s + wn ** 2) / (s ** 2 + 2 * bd * wn * s + wn ** 2)
            Kf = -cm.Kp_float * (fw ** 2 / (s ** 2 + 2 * fz * fw * s + fw ** 2)) * (s / (s + hp)) * notch / s * pa
            e = max(np.max(np.abs(K[:, 0, 0] - Ks) / np.abs(Ks)), np.max(np.abs(K[:, 0, 1] - Kf) / np.abs(Kf)))
        else:
            Ks = -(cm.VS_Kp + cm.VS_Ki / s) * lpf
            e = np.max(np.abs(K[:, 0, 0] - Ks) / np.abs(Ks))
        worst_ctrl = max(worst_ctrl, float(e))
        # vectorised loop evaluation vs direct state-space loop system
        Lss = cm.loop_system()
        Ld = lti.freqresp(Lss.A, Lss.B, Lss.C, Lss.D, np.abs(s))[:, 0, 0]
        Lf = cm.loop_eval(s)
        worst_loop = max(worst_loop, float(np.max(np.abs(Lf - Ld) / np.abs(Ld))))
        # closed-loop poles that moved away from the open-loop ones must be roots of 1 + L
        A = cm.closed_loop()[0]
        lam = np.linalg.eigvals(A)
        lam_ol = np.linalg.eigvals(Lss.A)
        moved = [l for l in lam if np.min(np.abs(lam_ol - l)) > 1e-4 * max(1.0, abs(l)) and abs(l) < 20]
        if moved:
            vals = np.abs(1 + cm.loop_eval(np.array(moved)))
            Lmag = np.abs(cm.loop_eval(np.array(moved)))
            worst_root = max(worst_root, float(np.max(vals / np.maximum(1, Lmag))))
        r = CL.loop_analysis(cm)
        nyq_ok &= r['nyquist_ok']
    out['IEA all ops: controller K(jw) vs analytic ROSCO filter/PI chain, max rel diff'] = worst_ctrl
    out['IEA all ops: vectorised L(jw) vs state-space loop, max rel diff'] = worst_loop
    out['IEA all ops: max |1+L(lambda)| / max(1,|L|) at every closed-loop pole that moved'] = worst_root
    out['IEA all ops: Nyquist Z = N + P holds'] = float(nyq_ok)
    assert worst_ctrl < 1e-8 and worst_loop < 1e-8 and worst_root < 1e-6 and nyq_ok
    return out


if __name__ == '__main__':
    res = {}
    for f in (check_whirl, check_tf, check_closed_loop):
        res.update(f())
    for k, v in res.items():
        print(f'{k:88s} {v:.3e}')
    print('engine checks OK')
