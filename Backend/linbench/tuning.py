"""ROSCO's 1-DOF rotor model and its PI tuning, re-implemented without the ROSCO package.

Mirrors rosco.toolbox.controller.Controller.tune_controller (variable-pitch path,
VS_FBP = 0) so the design model can be compared with the full linearisation.
Validated against ROSCO itself in tests/test_tuning.py.
"""
from __future__ import annotations

import numpy as np
from scipy import interpolate


class Surface:
    """RotorPerformance equivalent: table[n_tsr, n_pitch] on (pitch_rad, tsr)."""

    def __init__(self, table, pitch_rad, tsr):
        self.table, self.pitch, self.tsr = np.asarray(table), np.asarray(pitch_rad), np.asarray(tsr)
        self.g_tsr, self.g_pitch = np.gradient(self.table)
        self._s = interpolate.RectBivariateSpline(self.pitch, self.tsr, self.table.T)
        self._gb = interpolate.RectBivariateSpline(self.pitch, self.tsr, self.g_pitch.T)
        self._gt = interpolate.RectBivariateSpline(self.pitch, self.tsr, self.g_tsr.T)
        idx = np.where(self.table == self.table.max())
        self.max = float(self.table.max())
        self.tsr_opt = float(self.tsr[idx[0][-1]])
        self.pitch_opt = float(self.pitch[idx[1][-1]])

    def at(self, pitch, tsr):
        return np.squeeze(self._s(pitch, tsr).T)

    def grad(self, pitch, tsr):
        """(dC/dpitch, dC/dTSR) per rad and per unit TSR."""
        dp = float(np.squeeze(self._gb(pitch, tsr))) / np.diff(self.pitch)[0]
        dt = float(np.squeeze(self._gt(pitch, tsr))) / np.diff(self.tsr)[0]
        return dp, dt


def _sigma(tt, t0, t1, y0, y1):
    a3 = 2 / (t0 - t1) ** 3
    a2 = -3 * (t0 + t1) / (t0 - t1) ** 3
    a1 = 6 * t1 * t0 / (t0 - t1) ** 3
    a0 = (t0 - 3 * t1) * t0 ** 2 / (t0 - t1) ** 3
    return (y1 - y0) * (np.vander(tt, N=4) @ np.array([a3, a2, a1, a0])) + y0


def multi_sigma(xx, xb, yb):
    xx = np.asarray(xx, float)
    xb, yb = [float(x) for x in xb], [float(y) for y in yb]
    yy = np.zeros_like(xx)
    for i in range(len(xb) - 1):
        if xb[i + 1] == xb[i]:          # repeated breakpoint: zero-width segment
            continue
        m = (xx >= xb[i]) & (xx < xb[i + 1])
        yy[m] = _sigma(xx[m], xb[i], xb[i + 1], yb[i], yb[i + 1])
    yy[xx < xb[0]] = yb[0]
    yy[xx >= xb[-1]] = yb[-1]
    return yy


def _scalar_or_list(v):
    if isinstance(v, (list, tuple, np.ndarray)):
        return (v[0], False) if len(v) == 1 else (list(v), True)
    return v, False


def one_dof(cp: Surface, rho, R, J, Ng, v, tsr, pitch):
    """ROSCO plant coefficients at one operating point (generator-speed state).

    omega_g_dot = A*omega_g + B_beta*beta + B_tau*tau_g
    """
    if not (tsr > 0 and v > 0 and J):
        raise ValueError(f'1-DOF model needs TSR > 0, wind > 0 and J > 0 (got TSR={tsr}, U={v}, J={J})')
    Ar = np.pi * R ** 2
    Cp = float(cp.at(pitch, tsr))
    dCp_db, dCp_dl = cp.grad(pitch, tsr)
    dtau_db = Ng / 2 * rho * Ar * R * (1 / tsr) * dCp_db * v ** 2
    dtau_dl = Ng / 2 * rho * Ar * R * v ** 2 * (1 / tsr ** 2) * (dCp_dl * tsr - Cp)
    dtau_dw = dtau_dl * R / v / Ng
    return dict(A=dtau_dw / J, B_beta=dtau_db / J, B_tau=-Ng ** 2 / J, Cp=Cp,
                dCp_dbeta=dCp_db, dCp_dTSR=dCp_dl)


def tune(cp: Surface, turb: dict, ctrl: dict, ct: Surface = None):
    """Reproduce ROSCO's operating schedule and PI gain schedule.

    turb: J, rho, R, Ng, GenEff, GBoxEff, rated_rotor_speed, v_min, v_rated, v_max, TSR_operational, rated_power
    ctrl: omega_pc, zeta_pc, U_pc, interp_type, omega_vs, zeta_vs, min_pitch, WS_GS_n, PC_GS_n, VS_ConstPower
    """
    J, rho, R, Ng = turb['J'], turb['rho'], turb['R'], turb['Ng']
    w_rated = turb['rated_rotor_speed']
    WS_GS_n, PC_GS_n = int(ctrl.get('WS_GS_n', 60)), int(ctrl.get('PC_GS_n', 30))
    min_pitch = ctrl.get('min_pitch')
    if min_pitch is None:
        min_pitch = cp.pitch_opt
    TSR_oper = turb.get('TSR_operational') or cp.tsr_opt
    v_rated = turb['v_rated']
    TSR_rated = w_rated * R / v_rated
    v_br = np.linspace(turb['v_min'], v_rated, num=WS_GS_n - PC_GS_n)[:-1]
    v_ar = np.linspace(v_rated, turb['v_max'], num=PC_GS_n + 1)
    v = np.concatenate((v_br, v_ar))
    TSR_br = [min(TSR_oper, w_rated * R / vv) for vv in v_br]
    TSR_ar = w_rated * R / v_ar
    TSR_op = np.concatenate((TSR_br, TSR_ar))
    Cp_ar0 = cp.at(min_pitch, TSR_ar[0])
    Cp_op = np.concatenate(([cp.at(min_pitch, t) for t in TSR_br], Cp_ar0 * (TSR_ar / TSR_rated) ** 3))
    pitch_op = np.empty(len(v))
    dCp_b = np.empty(len(v)); dCp_l = np.empty(len(v))
    for i in range(len(v)):
        c_t = np.ravel(cp.at(cp.pitch, TSR_op[i]))
        k = c_t.argmax()
        Cp_op[i] = np.clip(Cp_op[i], np.min(c_t[k:]), np.max(c_t[k:]))
        f = interpolate.interp1d(c_t[k:], cp.pitch[k:])
        pitch_op[i] = min(min_pitch, f(Cp_op[i])) if v[i] <= v_rated else max(min_pitch, f(Cp_op[i]))
        dCp_b[i], dCp_l[i] = cp.grad(pitch_op[i], TSR_op[i])
    Ar = np.pi * R ** 2
    dtau_db = Ng / 2 * rho * Ar * R * (1 / TSR_op) * dCp_b * v ** 2
    dtau_dl = Ng / 2 * rho * Ar * R * v ** 2 * (1 / TSR_op ** 2) * (dCp_l * TSR_op - Cp_op)
    A = dtau_dl * (R / v / Ng) / J
    if ctrl.get('VS_ConstPower'):
        A[-len(v_ar) + 1:] += Ng ** 2 / J * turb['rated_power'] / (Ng ** 2 * w_rated ** 2)
    B_beta = dtau_db / J
    B_tau = -Ng ** 2 / J * np.ones(len(v))
    v_pc = v_ar[1:]
    om, om_l = _scalar_or_list(ctrl.get('omega_pc', 0.2))
    ze, ze_l = _scalar_or_list(ctrl.get('zeta_pc', 1.0))
    if not om_l and not ze_l:
        om_U, ze_U = om * np.ones(len(v_pc)), ze * np.ones(len(v_pc))
    elif ctrl.get('interp_type', 'sigma') == 'sigma':
        U_pc = ctrl['U_pc']
        om_U = multi_sigma(v_pc, U_pc, om if om_l else [om] * len(U_pc))
        ze_U = multi_sigma(v_pc, U_pc, ze if ze_l else [ze] * len(U_pc))
    else:
        U_pc = ctrl['U_pc']
        kind = ctrl.get('interp_type', 'linear')
        om_U = interpolate.interp1d(U_pc, om if om_l else [om] * len(U_pc), kind=kind, fill_value='extrapolate')(v_pc)
        ze_U = interpolate.interp1d(U_pc, ze if ze_l else [ze] * len(U_pc), kind=kind, fill_value='extrapolate')(v_pc)
    A_pc, B_pc = A[-len(v_ar) + 1:], B_beta[-len(v_ar) + 1:]
    pA, pB = np.polyfit(v_pc, A_pc, 1), np.polyfit(v_pc, B_pc, 1)
    A_lin, B_lin = pA[0] * v_pc + pA[1], pB[0] * v_pc + pB[1]
    Kp = 1 / B_lin * (2 * ze_U * om_U + A_lin)
    Ki = om_U ** 2 / B_lin
    A_vs = A[:len(v_br)]
    om_vs, ze_vs = ctrl.get('omega_vs', 0.2), ctrl.get('zeta_vs', 0.7)
    vs_Kp = 1 / B_tau[:len(v_br)] * (2 * ze_vs * om_vs + A_vs)
    vs_Ki = om_vs ** 2 / B_tau[:len(v_br)]
    return dict(v=v, TSR_op=TSR_op, Cp_op=Cp_op, pitch_op=pitch_op, A=A, B_beta=B_beta, B_tau=B_tau,
                v_pc=v_pc, A_pc_fit=A_lin, B_pc_fit=B_lin, omega_pc_U=om_U, zeta_pc_U=ze_U,
                pc_Kp=Kp, pc_Ki=Ki, PC_GS_angles=pitch_op[-len(v_ar) + 1:],
                vs_Kp=vs_Kp, vs_Ki=vs_Ki, min_pitch=float(min_pitch), v_rated=v_rated)


def turbine_from_sources(discon: dict = None, yaml_d: dict = None, fast: dict = None):
    """Assemble the turbine constants from whatever the user provided.  Returns (turb, ctrl, sources)."""
    discon, yaml_d, fast = discon or {}, yaml_d or {}, fast or {}
    tp = yaml_d.get('turbine_params', {}) or {}
    cp_ = yaml_d.get('controller_params', {}) or {}
    src = {}

    def pick(name, *cands):
        for label, val in cands:
            if isinstance(val, (int, float)) and not isinstance(val, bool) and np.isfinite(val):
                src[name] = label
                return float(val)
        return None
    Ng = pick('Ng', ('OpenFAST ED GBRatio', fast.get('GBRatio')), ('DISCON WE_GearboxRatio', discon.get('WE_GearboxRatio')))
    Ng = Ng or 1.0
    J = None
    if isinstance(tp.get('rotor_inertia'), (int, float)) and fast.get('GenIner') is not None:
        J = float(tp['rotor_inertia']) + float(fast['GenIner']) * Ng ** 2
        src['J'] = 'YAML rotor_inertia + ED GenIner*Ng^2'
    if J is None:
        J = pick('J', ('DISCON WE_Jtot', discon.get('WE_Jtot')))
    turb = dict(
        J=J, Ng=Ng,
        rho=pick('rho', ('OpenFAST AirDens', fast.get('AirDens')), ('DISCON WE_RhoAir', discon.get('WE_RhoAir'))),
        R=pick('R', ('OpenFAST ED TipRad', fast.get('TipRad')), ('DISCON WE_BladeRadius', discon.get('WE_BladeRadius'))),
        GenEff=pick('GenEff', ('OpenFAST SrvD GenEff', fast.get('GenEff')), ('DISCON VS_GenEff', discon.get('VS_GenEff'))) or 100.0,
        GBoxEff=pick('GBoxEff', ('OpenFAST ED GBoxEff', fast.get('GBoxEff'))) or 100.0,
        rated_rotor_speed=pick('rated_rotor_speed', ('YAML rated_rotor_speed', tp.get('rated_rotor_speed')),
                               ('DISCON PC_RefSpd/Ng', (discon.get('PC_RefSpd') or np.nan) / Ng)),
        rated_power=pick('rated_power', ('YAML rated_power', tp.get('rated_power')), ('DISCON VS_RtPwr', discon.get('VS_RtPwr'))),
        v_min=pick('v_min', ('YAML v_min', tp.get('v_min'))) or 3.0,
        v_max=pick('v_max', ('YAML v_max', tp.get('v_max'))) or 25.0,
        v_rated=pick('v_rated', ('YAML v_rated', tp.get('v_rated')),
                     ('DISCON IPC_Vramp[1]', (discon.get('IPC_Vramp') or [np.nan, np.nan])[-1]
                      if isinstance(discon.get('IPC_Vramp'), list) else None)),
        TSR_operational=pick('TSR_operational', ('YAML TSR_operational', tp.get('TSR_operational')),
                             ('DISCON VS_TSRopt', discon.get('VS_TSRopt'))),
    )
    ctrl = dict(omega_pc=cp_.get('omega_pc'), zeta_pc=cp_.get('zeta_pc'), U_pc=cp_.get('U_pc'),
                interp_type=cp_.get('interp_type', 'sigma'), omega_vs=cp_.get('omega_vs', 0.2),
                zeta_vs=cp_.get('zeta_vs', 0.7), min_pitch=cp_.get('min_pitch', None),
                WS_GS_n=cp_.get('WS_GS_n', 60), PC_GS_n=cp_.get('PC_GS_n', 30),
                VS_ConstPower=cp_.get('VS_ConstPower', discon.get('VS_ConstPower', 0)))
    if ctrl['omega_pc'] is None:
        ctrl['omega_pc'] = 0.2
    if ctrl['zeta_pc'] is None:
        ctrl['zeta_pc'] = 1.0
    return turb, ctrl, src
