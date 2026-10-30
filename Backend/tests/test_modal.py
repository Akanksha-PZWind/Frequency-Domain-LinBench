"""Pole/residue exports used by the browser to redraw curves over any panned or zoomed window.

1. Open loop (synthetic rotor, every standard input/output pair at every operating point):
   the exported form matches the direct state-space frequency response from 1e-6 to 1e4 rad/s
   (wider than the band it is checked on when exported), step and impulse responses match the
   exact discretisation out to three times the default duration, and the pole-zero phase is
   continuous and equal to numpy's unwrapped phase up to a constant multiple of 360 deg.
2. Closed loop (IEA-15MW + DISCON): L(jw) and the controller K(jw) from the exports match
   ControllerModel.loop_eval / ctrl_eval at every operating point.
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)
from linbench import lti  # noqa: E402
from linbench.discover import scan_folder  # noqa: E402
from linbench.model import OpModel  # noqa: E402
from linbench.tf import SISO  # noqa: E402
from _paths import SYN, IEA, DISCON  # noqa: E402


def _unpack(m):
    p = np.array([complex(*x) if isinstance(x, (list, tuple)) else x for x in m['p']], complex)
    r = np.array([complex(*x) if isinstance(x, (list, tuple)) else x for x in m['r']], complex)
    z = np.array([complex(*x) if isinstance(x, (list, tuple)) else x for x in m['z']], complex)
    return p, r, z


def _phase(m, p, z, w, G):
    pf = np.concatenate([p, np.conj(p[p.imag > 0])])
    return lti.modal_phase(dict(argk=m['argk'], off=m['off'], _z=z, _pfull=pf), w, G)


def check_open():
    out = {}
    info, ops = scan_folder(SYN)
    w = np.logspace(-6, 4, 3000)
    e_f = e_t = e_ph = 0.0
    n_cases = n_phase = 0
    for g in ops:
        om = OpModel(g.files)
        si, so = om.std_inputs(), om.std_outputs()
        pairs = [(si[a], so[b]) for a in ('pitch', 'torque', 'wind') for b in ('genspeed', 'nacacc_fa', 'ttdspfa')
                 if si[a] is not None and so[b] is not None]
        for i, o in pairs:
            s = SISO(om, i, o)
            m = lti.modal_export(om.A, s.b, s.c, s.d, s.scale)
            assert m is not None, (om.wind, i, o)
            p, r, z = _unpack(m)
            Gd = lti.freqresp(om.A, s.b[:, None], s.c[None, :], np.array([[s.d]]), w)[:, 0, 0] * s.scale
            if not len(p) and m['d'] == 0:       # channel pair not connected: exported as G = 0
                nat = abs(s.scale) * np.linalg.norm(s.c) * np.linalg.norm(s.b)
                assert np.abs(Gd).max() < 1e-12 * nat, (om.wind, i, o)
                n_zero = out.get('open loop: pairs recognised as not connected (G = 0)', 0.0)
                out['open loop: pairs recognised as not connected (G = 0)'] = n_zero + 1
                continue
            Gm = lti.modal_freq(p, r, m['d'], w)
            e_f = max(e_f, float(np.max(np.abs(Gm - Gd)) / np.abs(Gd).max()))
            n_cases += 1
            if m['phase_ok']:
                n_phase += 1
                sig = np.abs(Gd) > 1e-6 * np.abs(Gd).max()
                d = _phase(m, p, z, w, Gm) - np.unwrap(np.angle(Gd))
                d = d[sig]
                e_ph = max(e_ph, float(np.ptp(d - 2 * np.pi * np.round((d - d[0]) / (2 * np.pi)))))
                # constant offset: no hidden 360-deg jumps between samples where G is significant
                assert np.ptp(np.round((d - d[0]) / (2 * np.pi))) == 0, (om.wind, i, o)
            for kind in ('step', 'impulse'):
                _, _, T = s.time(kind, 1.0)
                t, y = lti.step_impulse(om.A, s.b, s.c, s.d, kind, 1.0, 3 * T, 1500)
                ym = lti.modal_time(p, r, m['d'], t, kind, 1.0)
                e_t = max(e_t, float(np.max(np.abs(ym - y * s.scale)) / (np.abs(y * s.scale).max() or 1.0)))
    assert e_f < 1e-8, e_f
    assert e_t < 1e-8, e_t
    assert e_ph < 1e-6, e_ph
    out['open loop: cases exported (synthetic, all ops x standard pairs)'] = float(n_cases)
    out['open loop: max |G_modal - G_ss| / max|G| over 1e-6..1e4 rad/s'] = e_f
    out['open loop: step/impulse max rel error out to 3x duration'] = e_t
    out['open loop: cases with pole-zero phase'] = float(n_phase)
    out['open loop: phase vs numpy unwrap, spread after removing whole turns [rad]'] = e_ph
    return out


def check_closed_synthetic():
    out = check_closed(SYN, os.path.join(SYN, 'synthetic_DISCON.IN'))
    n = len(scan_folder(SYN)[1])
    # every point must export: a silent fallback would hide a regression (as a PI integrator once did)
    assert out['closed loop: operating points with an exported L / K'] == n, out
    return out


def check_closed(folder=None, discon=None):
    from linbench import closedloop as CL, rosco_io
    dis = rosco_io.read_discon(discon or DISCON)
    info, ops = scan_folder(folder or IEA)
    w = np.logspace(-4, 2, 1500)
    e_L = e_K = 0.0
    n_L = n_K = 0
    for g in ops:
        om = OpModel(g.files)
        cm = CL.ControllerModel(om, dis)
        Lss = cm.loop_system()
        mL = lti.modal_export(Lss.A, Lss.B[:, 0], Lss.C[0], Lss.D[0, 0])
        if mL is not None:
            p, r, _ = _unpack(mL)
            Ld = cm.loop_eval(1j * w)
            e_L = max(e_L, float(np.max(np.abs(lti.modal_freq(p, r, mL['d'], w) - Ld)) / np.abs(Ld).max()))
            n_L += 1
        K = cm._prep_eval()['Kall']
        mK = lti.modal_export(K.A, K.B[:, 0], K.C[0], K.D[0, 0])
        if mK is not None:
            p, r, _ = _unpack(mK)
            Kd = cm.ctrl_eval(1j * w)[:, 0, 0]
            e_K = max(e_K, float(np.max(np.abs(lti.modal_freq(p, r, mK['d'], w) - Kd)) / np.abs(Kd).max()))
            n_K += 1
    assert e_L < 1e-6 and e_K < 1e-6, (e_L, e_K)
    return {'closed loop: operating points with an exported L / K': float(min(n_L, n_K)),
            'closed loop: max rel error of exported L(jw) vs loop_eval': e_L,
            'closed loop: max rel error of exported K(jw) vs ctrl_eval': e_K}


if __name__ == '__main__':
    for k, v in check_open().items():
        print(f'{k:80s} {v:.3e}')
    if IEA:
        for k, v in check_closed().items():
            print(f'{k:80s} {v:.3e}')
    print('Modal export validation OK')
