"""Closed-loop stability for varied controller gains.

The ROSCO model of closedloop.ControllerModel is rebuilt with scaled gains and closed around
the same linearisation; stability is read from the eigenvalues of the closed-loop A.

Metric per gain setting, over closed-loop poles lambda with |lambda| <= 2 pi fmax:
    zeta_min = min(-Re lambda / |lambda|)       (negative <=> an unstable pole; real poles give +-1)
Poles that coincide with an open-loop plant pole are left out of zeta_min: the loop cannot see or
move them (uncontrollable from the actuator or unobservable in the measurements), so they would
otherwise pin the map to one value.  They are listed as 'unaffected' and still count for
'stable' through max Re lambda over all poles.

Gain pairs
  'pi'    : x = factor on Kp, y = factor on Ki of the active loop (pitch PI in Region 3, torque PI in
            Region 2); 1 x 1 is the DISCON value at this operating point.
  'float' : x = factor on both PI gains, y = factor on the floating feedback gain Kp_float.
"""
from __future__ import annotations

import copy
import math

import numpy as np
from scipy import linalg

from . import closedloop as CL
from . import lti
from .model import unit_convert

TOL = 1e-7


def with_gains(cm, fx=1.0, fy=1.0, pair='pi'):
    """Copy of a ControllerModel with scaled gains (DISCON values x factor)."""
    c = copy.copy(cm)
    c.notes = list(cm.notes)
    c._ev = None
    kx, ky = (fx, fy) if pair == 'pi' else (fx, fx)
    if cm.region == 'pitch':
        c.Kp, c.Ki = cm.Kp * kx, cm.Ki * ky
    else:
        c.VS_Kp, c.VS_Ki = cm.VS_Kp * kx, cm.VS_Ki * ky
    if pair == 'float':
        c.Kp_float = cm.Kp_float * fy
    c.build()
    c.notes = list(cm.notes)
    return c


def gains_of(cm):
    if cm.region == 'pitch':
        return dict(kp=cm.Kp, ki=cm.Ki, kp_name='Kp', ki_name='Ki', kp_unit='s', ki_unit='–')
    return dict(kp=cm.VS_Kp, ki=cm.VS_Ki, kp_name='VS_Kp', ki_name='VS_Ki', kp_unit='N·m·s/rad', ki_unit='N·m/rad')


class Metric:
    """Per closed-loop system:
      zeta / fn       least-damped pole the loop can move (|lambda| <= 2 pi fmax)
      zeta_sp / wn_sp speed-loop pole: largest participation of rotor speed + PI integrator (< 0.5 Hz),
                      the pole ROSCO places with omega_pc / zeta_pc
      max_re          largest real part over all poles (> 0 => unstable)"""

    def __init__(self, cm, fmax_hz=2.0):
        self.ol = linalg.eigvals(cm.om.A)
        self.wmax = 2 * math.pi * fmax_hz

    def __call__(self, A, states=None):
        if states is not None:
            lam, V = linalg.eig(A)
        else:
            lam, V = linalg.eigvals(A), None
        max_re = float(lam.real.max())
        mag = np.abs(lam)
        near = np.min(np.abs(lam[:, None] - self.ol[None, :]), axis=1) <= 1e-7 * np.maximum(1.0, mag) if self.ol.size \
            else np.zeros(len(lam), bool)
        sel = (mag <= self.wmax) & (mag > 1e-9) & ~near
        out = dict(zeta=None, fn=None, zeta_sp=None, wn_sp=None, max_re=max_re, stable=bool(max_re <= TOL),
                   unaffected=lam[near])
        if sel.any():
            z = -lam[sel].real / mag[sel]
            k = int(np.argmin(z))
            out.update(zeta=float(z[k]), fn=float(mag[sel][k] / (2 * math.pi)))
        if V is not None:
            try:
                W = np.linalg.inv(V)
            except np.linalg.LinAlgError:
                W = np.linalg.pinv(V)
            idx = [i for i, s in enumerate(states) if 'Rotor azimuth' in s or 'PI integrator' in s]
            if idx:
                P = np.abs(W * V.T)                         # P[k, i] = |w_ki v_ik|
                P = P / np.maximum(P.sum(axis=1, keepdims=True), 1e-300)
                score = P[:, idx].sum(axis=1)
                cand = np.where((mag < math.pi) & (mag > 1e-9) & (lam.imag >= -1e-9))[0]
                if cand.size:
                    k = cand[int(np.argmax(score[cand]))]
                    out.update(zeta_sp=float(-lam[k].real / mag[k]), wn_sp=float(mag[k]))
        return out


def gain_map(cm, pair='pi', lo=0.25, hi=4.0, n=25, fmax_hz=2.0):
    fs = np.geomspace(lo, hi, n)
    met = Metric(cm, fmax_hz)
    grids = {k: np.full((n, n), np.nan) for k in ('zeta', 'fn', 'zeta_sp', 'wn_sp', 'max_re')}
    unaffected = None
    for iy, fy in enumerate(fs):
        for ix, fx in enumerate(fs):
            A, _, _, _, states = with_gains(cm, fx, fy, pair).closed_loop()
            m = met(A, states)
            for k, g in grids.items():
                g[iy, ix] = np.nan if m[k] is None else m[k]
            if unaffected is None:
                unaffected = m['unaffected']
    A, _, _, _, states = cm.closed_loop()
    base = met(A, states)
    return dict(fx=fs, fy=fs, pair=pair, **grids,
                base={k: base[k] for k in ('zeta', 'fn', 'zeta_sp', 'wn_sp', 'max_re', 'stable')},
                unaffected=_label_unaffected(cm, unaffected if unaffected is not None else np.zeros(0)))


def _label_unaffected(cm, lam):
    from .modal import analyse_modes
    modes = analyse_modes(cm.om)
    out = []
    for l in lam:
        if l.imag < -1e-9 or abs(l) < 1e-9:
            continue
        m = min(modes, key=lambda m: abs(complex(m['re'], abs(m['im'])) - complex(l.real, abs(l.imag))))
        out.append(dict(label=m['label'], fn=abs(l) / (2 * math.pi), zeta=float(-l.real / abs(l))))
    seen, uniq = set(), []
    for u in sorted(out, key=lambda d: d['fn']):
        if u['label'] not in seen:
            seen.add(u['label'])
            uniq.append(u)
    return uniq


def stable_range(cm, pair='pi', lo=0.05, hi=20.0, n=41):
    """Interval of a common factor k on both PI gains that keeps the loop stable (bisection refined)."""
    met = Metric(cm)
    ks = np.geomspace(lo, hi, n)
    st = np.array([met(with_gains(cm, k, k, pair).closed_loop()[0])['stable'] for k in ks])

    def edge(a, b, sa):
        for _ in range(30):
            m = math.sqrt(a * b)
            if met(with_gains(cm, m, m, pair).closed_loop()[0])['stable'] == sa:
                a = m
            else:
                b = m
        return math.sqrt(a * b)
    if not st.any():
        return None, None
    i1 = int(np.argmax(ks >= 1.0))
    if not st[i1]:
        return None, None
    j = i1
    while j > 0 and st[j - 1]:
        j -= 1
    k_lo = None if j == 0 else edge(ks[j], ks[j - 1], True)
    j = i1
    while j < n - 1 and st[j + 1]:
        j += 1
    k_hi = None if j == n - 1 else edge(ks[j], ks[j + 1], True)
    return k_lo, k_hi


def scaling_sweep(cms, factors=None, fmax_hz=2.0):
    """zeta_min vs a common PI gain factor at every operating point."""
    if factors is None:
        factors = np.geomspace(0.25, 4.0, 17)
    rows = []
    for i, cm in cms:
        if cm is None:
            continue
        met = Metric(cm, fmax_hz)
        z, zs, st = [], [], []
        for k in factors:
            A, _, _, _, states = with_gains(cm, k, k, 'pi').closed_loop()
            m = met(A, states)
            z.append(m['zeta'])
            zs.append(m['zeta_sp'])
            st.append(m['stable'])
        k_lo, k_hi = stable_range(cm)
        g = gains_of(cm)
        rows.append(dict(op=i, wind=cm.om.wind, region=cm.region, zeta=z, zeta_sp=zs, stable=st, k_lo=k_lo,
                         k_hi=k_hi, kp=g['kp'], ki=g['ki'], stable_1=met(cm.closed_loop()[0])['stable']))
    return dict(factors=np.asarray(factors), rows=rows)


def _time_T(A):
    lam = linalg.eigvals(A)
    sl = [abs(l.real) for l in lam if abs(l.real) > 1e-5 and abs(l) < 20]
    return float(np.clip(6.0 / min(sl), 30.0, 600.0)) if sl else 200.0


def point(cm, fx, fy, pair='pi', i_in=None):
    """Margins, poles, loop shape and step responses for one trial gain setting (and the DISCON one)."""
    om = cm.om
    si, so = om.std_inputs(), om.std_outputs()
    if i_in is None:
        i_in = si['wind'] if si['wind'] is not None else cm.act_input
    out = {}
    for key, c in (('base', cm), ('trial', with_gains(cm, fx, fy, pair))):
        r = CL.loop_analysis(c)
        poles = CL.label_cl_poles(c, fmax_hz=1.0)
        A, B, C, D, _ = c.closed_loop()
        Lss = c.loop_system()
        T = _time_T(A)
        fin = unit_convert(om.u[i_in].unit)[1]
        g = so['genspeed']
        f_g = unit_convert(om.y[g].unit)[1]
        na = om.C.shape[0]
        f_a = 180 / math.pi if c.act_unit == 'rad' else 1.0
        met = Metric(c)(A, c.closed_loop()[4])
        gg = gains_of(c)
        out[key] = dict(
            kp=gg['kp'], ki=gg['ki'], kp_float=c.Kp_float, stable=r['stable'], Z=r['Z'],
            gm=r['gm_min'], pm=r['pm_min'], s_m=r['s_m'], w_sm=r['w_sm'], Smax=r['Smax'], dm=r['delay_margin'],
            bw_T=r['bw_T'], zeta_min=met['zeta'], fn_min=met['fn'], max_re=met['max_re'],
            zeta_sp=met['zeta_sp'], wn_sp=met['wn_sp'],
            poles=poles, T=T,
            L_modal=lti.modal_export(Lss.A, Lss.B[:, 0], Lss.C[0], Lss.D[0, 0]),
            y_modal=lti.modal_export(A, B[:, i_in], C[g], D[g, i_in], f_g / fin, time=(T, 'step')),
            u_modal=lti.modal_export(A, B[:, i_in], C[na], D[na, i_in], f_a / fin, time=(T, 'step')))
        # exact sampled responses: initial view, and fallback if a pole/residue export was refused
        t, y = lti.step_impulse(A, B[:, i_in], C[g], D[g, i_in], 'step', 1.0 / fin, T, 800)
        _, u = lti.step_impulse(A, B[:, i_in], C[na], D[na, i_in], 'step', 1.0 / fin, T, 800)
        out[key].update(t=t, y=y * f_g, u=u * f_a)
    g = so['genspeed']
    out.update(fx=fx, fy=fy, pair=pair, wind=om.wind, region=cm.region, act=cm.act_name,
               act_unit='deg' if cm.act_unit == 'rad' else cm.act_unit,
               input=dict(label=om.u_label[i_in], unit=unit_convert(om.u[i_in].unit)[0]),
               output=dict(label=om.y_label[g], unit=unit_convert(om.y[g].unit)[0]), names=gains_of(cm))
    return out
