"""ROSCO controller as a continuous-time LTI system, closed around the linearised plant.

Signal path reproduced from ROSCO (Filters.f90 / Controllers.f90):
  gen speed -> LPF (1st/2nd order) -> gen-speed notches -> PI (gain-scheduled on pitch) -> + floating
  nacelle accel -> LPF2 (F_FlCornerFreq) -> HPF (F_FlHighPassFreq) -> tower-top notches -> 1/s -> x(-Kp_float)
  pitch command -> pitch-actuator filter (PA_Mode) -> plant collective pitch
  torque: Region 3 constant torque (or constant power, VS_ConstPower=1); Region 2 torque PI (VS_KP/VS_KI)
  or K*omega^2 (VS_ControlMode 1).
Controller discretisation, saturations, rate limits, set-point smoothing and the wind-speed
estimator are not part of the linear model.
"""
from __future__ import annotations

import math

import numpy as np
from scipy import linalg, optimize

from . import lti
from .lti import SS
from .modal import analyse_modes
from .rosco_io import as_list

RPM2RADS = math.pi / 30


def _interp(x, xp, fp):
    xp, fp = np.asarray(as_list(xp), float), np.asarray(as_list(fp), float)
    if len(xp) == 0 or len(fp) == 0:
        return None
    if len(xp) == 1 or len(fp) == 1:
        return float(fp[0])
    o = np.argsort(xp)
    return float(np.interp(x, xp[o], fp[o]))


def notch_sets(dis):
    """Return (genspeed_notches, towertop_notches) as lists of (w, beta_num, beta_den)."""
    gs, tt = [], []
    if 'F_NumNotchFilts' in dis:
        f = as_list(dis.get('F_NotchFreqs'))
        bn = as_list(dis.get('F_NotchBetaNum')) or [0.0] * len(f)
        bd = as_list(dis.get('F_NotchBetaDen')) or [0.25] * len(f)
        for key, dst in (('F_GenSpdNotch', gs), ('F_TwrTopNotch', tt)):
            nn = int(as_list(dis.get(key + '_N'))[0]) if as_list(dis.get(key + '_N')) else 0
            ind = [int(i) for i in as_list(dis.get(key + '_Ind'))][:nn]
            for i in ind:
                if 1 <= i <= len(f) and f[i - 1] > 0:
                    dst.append((f[i - 1], bn[i - 1], bd[i - 1]))
    elif dis.get('F_NotchType', 0):          # ROSCO < 2.5
        nt = int(dis['F_NotchType'])
        w = as_list(dis.get('F_NotchCornerFreq'))
        b = as_list(dis.get('F_NotchBetaNumDen')) or [0.0, 0.25]
        if w:
            if nt in (1, 3):
                gs.append((w[0], b[0], b[1]))
            if nt in (2, 3):
                tt.append((w[0], b[0], b[1]))
    return gs, tt


class ControllerModel:
    """Linear ROSCO controller for one operating point."""

    def __init__(self, om, dis, region='auto', include=None):
        self.om, self.dis = om, dis
        inc = dict(lpf=True, notch=True, floating=True, actuator=True)
        inc.update(include or {})
        self.include = inc
        tr = om.trim()
        self.trim = tr
        self.si, self.so = om.std_inputs(), om.std_outputs()
        self.notes = []
        # -------------------------------------------------------------- region
        pitch = tr['pitch_rad']
        minpit = _interp(om.wind, dis.get('PS_WindSpeeds', []), dis.get('PS_BldPitchMin', [])) \
            if dis.get('PS_Mode', 0) else None
        if minpit is None:
            minpit = float(as_list(dis.get('PC_MinPit', 0.0))[0]) if dis.get('PC_MinPit') is not None else 0.0
        rt = float(dis.get('VS_RtTq', np.nan)) if dis.get('VS_RtTq') is not None else np.nan
        tq = tr['gen_torque']
        Ng = float(dis.get('WE_GearboxRatio', 1.0) or 1.0)
        w_rated_rpm = float(dis.get('PC_RefSpd', np.nan)) * 30 / math.pi if dis.get('PC_RefSpd') else np.nan
        gen_rpm = tr['gen_rpm'] if np.isfinite(tr['gen_rpm']) else tr['rotor_rpm'] * Ng
        at_rated_speed = np.isfinite(w_rated_rpm) and gen_rpm >= 0.98 * w_rated_rpm
        tq_sat = np.isfinite(rt) and np.isfinite(tq) and tq >= 0.98 * rt
        if region == 'auto':
            region = 'pitch' if at_rated_speed else 'torque'
            if tq_sat and not at_rated_speed:
                self.notes.append('Transition: generator torque at its limit but rotor below rated speed; '
                                  'in the time domain both loops may be saturated here.')
        # 'both' / 'both_tq': pitch PI and torque PI act on generator speed at the same time (the
        # transition around rated).  The loop is broken at pitch ('both') or at torque ('both_tq')
        # with the other loop left closed.  self.region stays the MAIN loop ('pitch' or 'torque').
        self.both = region in ('both', 'both_tq')
        region = {'both': 'pitch', 'both_tq': 'torque'}.get(region, region)
        self.region = region
        self.region_name = 'Region 3 · pitch control' if region == 'pitch' else 'Region 2 · torque control'
        if self.both:
            self.region_name = ('Region 2½ · pitch + torque (loop broken at ' + ('pitch' if region == 'pitch' else 'torque') + ')')
        self.min_pitch = minpit
        # -------------------------------------------------------------- gains
        self.Kp = _interp(pitch, dis.get('PC_GS_angles', [0]), dis.get('PC_GS_KP', [0])) or 0.0
        self.Ki = _interp(pitch, dis.get('PC_GS_angles', [0]), dis.get('PC_GS_KI', [0])) or 0.0
        self.VS_Kp = float(as_list(dis.get('VS_KP', 0.0))[0])
        self.VS_Ki = float(as_list(dis.get('VS_KI', 0.0))[0])
        fl_mode = int(dis.get('Fl_Mode', 0) or 0)
        self.fl_mode = fl_mode if inc['floating'] else 0
        self.Kp_float = _interp(om.wind, dis.get('Fl_U', [0]), dis.get('Fl_Kp', [0])) if fl_mode else 0.0
        self.build()

    # ------------------------------------------------------------------ blocks
    def _speed_filter(self):
        d = self.dis
        blocks = []
        if self.include['lpf']:
            wc = float(d.get('F_LPFCornerFreq', 0) or 0)
            if wc > 0:
                if int(d.get('F_LPFType', 1)) == 2:
                    blocks.append(lti.lpf2(wc, float(d.get('F_LPFDamping', 0.7)), 'Gen-speed LPF'))
                else:
                    blocks.append(lti.lpf1(wc, 'Gen-speed LPF'))
        gs, _ = notch_sets(d)
        if self.include['notch']:
            for w, bn, bd in gs:
                blocks.append(lti.notch(w, bn, bd, 'Gen-speed notch'))
        return blocks

    def _float_path(self):
        d = self.dis
        fc = as_list(d.get('F_FlCornerFreq', [0, 1]))
        blocks = []
        if fc and fc[0] > 0:
            blocks.append(lti.lpf2(fc[0], fc[1] if len(fc) > 1 else 1.0, 'Float LPF'))
        hp = float(as_list(d.get('F_FlHighPassFreq', 0.0))[0]) if d.get('F_FlHighPassFreq') is not None else 0.0
        if hp > 0:
            blocks.append(lti.hpf1(hp, 'Float HPF'))
        _, tt = notch_sets(d)
        if self.include['notch']:
            for w, bn, bd in tt:
                blocks.append(lti.notch(w, bn, bd, 'Tower-top notch'))
        blocks.append(lti.integrator('Float integrator'))
        blocks.append(lti.gain(-self.Kp_float))
        return blocks

    def _actuator(self):
        d = self.dis
        pa = int(d.get('PA_Mode', 0) or 0)
        w = float(d.get('PA_CornerFreq', 0) or 0)
        if not self.include['actuator'] or pa == 0 or w <= 0:
            return None
        if pa == 1:
            return lti.lpf1(w, 'Pitch actuator')
        return lti.lpf2(w, float(d.get('PA_Damping', 0.707)), 'Pitch actuator')

    def measurement_rows(self):
        """Rows (c, d) over plant states/inputs for the signals the controller reads."""
        om = self.om
        rows = {}
        g = self.so['genspeed']
        if g is None:
            raise ValueError('No generator speed output (ED GenSpeed) in the linearisation outputs.')
        rows['genspeed'] = (om.C[g] * RPM2RADS, om.D[g] * RPM2RADS, 'ED GenSpeed [rad/s]')
        if self.fl_mode:
            if self.fl_mode == 2:
                r = self.so['nacracc']
                if r is not None:
                    rows['float'] = (om.C[r] * math.pi / 180, om.D[r] * math.pi / 180, 'ED NcIMURAys [rad/s^2]')
                else:
                    iv = [i for i, ch in enumerate(om.x) if 'DOF_P)' in ch.desc and ch.name.lower().startswith('first time derivative')]
                    if iv:
                        rows['float'] = (om.A[iv[0]], om.B[iv[0]], 'platform pitch acceleration [rad/s^2]')
                        self.notes.append('Fl_Mode 2: NcIMURAys is not a linearisation output; platform pitch '
                                          'acceleration (state derivative) used instead (tower-top rotation neglected).')
                    else:
                        self.fl_mode = 0
                        self.notes.append('Fl_Mode 2 requested but no nacelle/platform pitch signal is available; floating feedback off.')
            else:
                r = self.om.find_output(r'\bYawBrTAxp\b') or self.so['nacacc_fa']
                if r is not None:
                    rows['float'] = (om.C[r], om.D[r], om.y_label[r] + ' [m/s^2]')
                else:
                    self.fl_mode = 0
                    self.notes.append('Fl_Mode 1 requested but no tower-top acceleration output; floating feedback off.')
        return rows

    def build(self):
        om = self.om
        rows = self.measurement_rows()
        self.meas_names = list(rows.keys())
        Cm = np.vstack([rows[k][0] for k in self.meas_names])
        Dm = np.vstack([rows[k][1] for k in self.meas_names])
        self.meas_labels = [rows[k][2] for k in self.meas_names]
        # controller: inputs = measurements, output = single actuator command (pitch or torque)
        nm = len(self.meas_names)
        fil = self._speed_filter()
        self.aux_input = self.si['torque']       # where the secondary feedback (if any) acts
        if self.region == 'pitch':
            pi_blk = lti.pi(-self.Kp, -self.Ki, 'Pitch PI integrator')     # beta = PI(ref - w) = -PI(w)
            spd = lti.chain(*(fil + [pi_blk])) if fil else pi_blk
            K = lti.select(spd, in_map=np.eye(nm)[[0]])
            if self.fl_mode and 'float' in rows:
                fl = lti.chain(*self._float_path())
                fl = lti.select(fl, in_map=np.eye(nm)[[self.meas_names.index('float')]])
                K = lti.parallel_sum(K, fl)
            act = self._actuator()
            if act is not None:
                K = lti.series(K, act)
            self.act_input = self.si['pitch']
            self.act_name = 'collective pitch'
            self.act_unit = 'rad'
            # torque in region 3: constant torque, or constant power feedback
            self.torque_fb = None
            if int(self.dis.get('VS_ConstPower', 0) or 0) == 1 and self.si['torque'] is not None:
                wg = self.trim['gen_rpm'] * RPM2RADS
                P = float(self.dis.get('VS_RtPwr', 0)) / (float(self.dis.get('VS_GenEff', 100)) / 100)
                k = -P / wg ** 2
                tfb = lti.chain(*(fil + [lti.gain(k)])) if fil else lti.gain(k)
                self.torque_fb = lti.select(tfb, in_map=np.eye(nm)[[0]])
            if self.both and self.si['torque'] is not None:
                # torque PI active as well (same filtered generator speed), kept closed
                tq = lti.pi(-self.VS_Kp, -self.VS_Ki, 'Torque PI integrator')
                self.torque_fb = lti.select(lti.chain(*(fil + [tq])) if fil else tq, in_map=np.eye(nm)[[0]])
        else:
            vsm = int(self.dis.get('VS_ControlMode', 2) or 0)
            if vsm == 1:
                wg = self.trim['gen_rpm'] * RPM2RADS
                k2 = float(self.dis.get('VS_Rgn2K', 0.0))
                blk = lti.gain(2 * k2 * wg)
                self.notes.append('VS_ControlMode 1: Region 2 torque = K*omega^2, linearised as 2*K*omega.')
            else:
                blk = lti.pi(-self.VS_Kp, -self.VS_Ki, 'Torque PI integrator')   # tau = PI(ref - w)
                self.notes.append('Torque-PI reference treated as constant (wind-speed-estimator dynamics not modelled).')
            K = lti.select(lti.chain(*(fil + [blk])) if fil else blk, in_map=np.eye(nm)[[0]])
            self.act_input = self.si['torque']
            self.act_name = 'generator torque'
            self.act_unit = 'N·m'
            self.torque_fb = None
            if self.both and self.si['pitch'] is not None:
                # pitch PI (+ floating feedback, + actuator) active as well, kept closed
                pp = lti.pi(-self.Kp, -self.Ki, 'Pitch PI integrator')
                aux = lti.select(lti.chain(*(fil + [pp])) if fil else pp, in_map=np.eye(nm)[[0]])
                if self.fl_mode and 'float' in rows:
                    fl = lti.select(lti.chain(*self._float_path()), in_map=np.eye(nm)[[self.meas_names.index('float')]])
                    aux = lti.parallel_sum(aux, fl)
                act = self._actuator()
                if act is not None:
                    aux = lti.series(aux, act)
                self.torque_fb = aux              # secondary feedback (name kept: it is the 'other' loop)
                self.aux_input = self.si['pitch']
        if self.act_input is None:
            raise ValueError(f'The linearisation has no {self.act_name} input.')
        self.K = K
        self.Cm, self.Dm = Cm, Dm

    # ------------------------------------------------------------------ interconnection
    def _assemble(self):
        """Controller with all active actuator outputs, and actuator placement matrix E."""
        nu = self.om.B.shape[1]
        if self.torque_fb is None:
            Kall = self.K
            cols = [self.act_input]
        else:
            T = self.torque_fb
            K = self.K
            Kall = SS(linalg.block_diag(K.A, T.A), np.vstack([K.B, T.B]), linalg.block_diag(K.C, T.C),
                      np.vstack([K.D, T.D]), K.states + T.states)
            cols = [self.act_input, self.aux_input]
        E = np.zeros((nu, len(cols)))
        for j, c in enumerate(cols):
            E[c, j] = 1.0
        return Kall, E

    def _system(self, E_fb, E_ext):
        """x' = A x + B u, u = d + E_fb a (+ E_ext w), a = controller(m). Returns matrices."""
        om = self.om
        Kall, _ = self._assemble()
        A, B, C, D = om.A, om.B, om.C, om.D
        Cm, Dm = self.Cm, self.Dm
        na = Kall.D.shape[0]
        M = np.linalg.inv(np.eye(na) - Kall.D @ Dm @ E_fb)
        # a = M (Dk Cm x + Ck xk + Dk Dm (d + E_ext w))
        ax, ak, ad = M @ Kall.D @ Cm, M @ Kall.C, M @ Kall.D @ Dm
        n, nk = A.shape[0], Kall.n
        Acl = np.block([[A + B @ E_fb @ ax, B @ E_fb @ ak],
                        [Kall.B @ (Cm + Dm @ E_fb @ ax), Kall.A + Kall.B @ Dm @ E_fb @ ak]])
        Bd = np.vstack([B + B @ E_fb @ ad, Kall.B @ (Dm + Dm @ E_fb @ ad)])     # input: d (nu)
        Cy = np.hstack([C + D @ E_fb @ ax, D @ E_fb @ ak])
        Dy = D + D @ E_fb @ ad
        Ca = np.hstack([ax, ak])
        Da = ad
        Bw = Bd @ E_ext if E_ext is not None else None
        states = self._plant_state_names() + Kall.states
        return dict(A=Acl, Bd=Bd, Cy=Cy, Dy=Dy, Ca=Ca, Da=Da, Bw=Bw, states=states)

    def loop_system(self, k=1.0):
        """Break the loop at the main actuator input: w -> plant -> controller -> z (= main command).
        Returns SS for L(s) = -k z/w, so that the closed loop is 1 + L = 0."""
        _, E = self._assemble()
        E_fb = E.copy()
        E_fb[:, 0] = 0.0
        S = self._system(E_fb, E[:, [0]])
        B = S['Bw']
        C = S['Ca'][[0], :]
        D = S['Da'][[0], :] @ E[:, [0]]
        return SS(S['A'], B, -k * C, -k * D, S['states'])

    def closed_loop(self, k=1.0):
        """All active loops closed (main loop scaled by k).  Inputs: every plant input as an additive
        disturbance.  Outputs: every plant output, then the actuator command(s)."""
        _, E = self._assemble()
        E_fb = E.copy()
        E_fb[:, 0] *= k
        S = self._system(E_fb, None)
        return S['A'], S['Bd'], np.vstack([S['Cy'], S['Ca']]), np.vstack([S['Dy'], S['Da']]), S['states']

    def _plant_state_names(self):
        return ['plant:' + d[0] for d in self.om.x_dof]

    # ------------------------------------------------------------------ fast frequency evaluation
    def _prep_eval(self):
        if getattr(self, '_ev', None) is not None:
            return self._ev
        om = self.om
        Kall, E = self._assemble()
        Bu = om.B @ E                    # plant: actuator cols
        Du = self.Dm @ E
        lam, V = linalg.eig(om.A)
        try:
            cond = np.linalg.cond(V)
        except Exception:
            cond = np.inf
        ev = dict(Kall=Kall, E=E, Du=Du, lam=lam, modal=cond < 1e10)
        if ev['modal']:
            W = np.linalg.inv(V)
            CV = self.Cm @ V             # (nm, n)
            WB = W @ Bu                  # (n, na)
            ev['R'] = np.einsum('ik,kj->kij', CV, WB)      # (n, nm, na)
        else:
            ev['Bu'] = Bu
        self._ev = ev
        return ev

    def plant_meas(self, s):
        """Gm(s): actuator commands -> measurements, shape (N, nm, na)."""
        ev = self._prep_eval()
        s = np.atleast_1d(np.asarray(s, complex))
        if ev['modal']:
            with np.errstate(divide='ignore', invalid='ignore'):
                X = 1.0 / (s[:, None] - ev['lam'][None, :])        # (N, n)
            G = np.einsum('sk,kij->sij', X, ev['R'])
        else:
            n = self.om.A.shape[0]
            G = np.empty((len(s), self.Cm.shape[0], ev['Bu'].shape[1]), complex)
            I = np.eye(n)
            for k0 in range(0, len(s), 200):
                ss = s[k0:k0 + 200]
                M = ss[:, None, None] * I[None] - self.om.A[None]
                X = np.linalg.solve(M, np.broadcast_to(ev['Bu'], (len(ss),) + ev['Bu'].shape))
                G[k0:k0 + 200] = np.einsum('ij,sjk->sik', self.Cm, X)
        return G + ev['Du'][None]

    def ctrl_eval(self, s):
        """Kall(s): measurements -> actuator commands, shape (N, na, nm)."""
        ev = self._prep_eval()
        K = ev['Kall']
        s = np.atleast_1d(np.asarray(s, complex))
        if K.n == 0:
            return np.broadcast_to(K.D, (len(s),) + K.D.shape).astype(complex)
        I = np.eye(K.n)
        M = s[:, None, None] * I[None] - K.A[None]
        X = np.linalg.solve(M, np.broadcast_to(K.B, (len(s),) + K.B.shape).astype(complex))
        return np.einsum('ij,sjk->sik', K.C, X) + K.D[None]

    def loop_eval(self, s, k=1.0):
        """L(s) at the main actuator (vectorised over s)."""
        G = self.plant_meas(s)
        Kc = self.ctrl_eval(s)
        na = G.shape[2]
        g0 = G[:, :, 0]                                 # (N, nm)
        k0 = Kc[:, 0, :]                                # (N, nm)
        if na == 1:
            z = np.einsum('si,si->s', k0, g0)
        else:
            Gr, Kr = G[:, :, 1:], Kc[:, 1:, :]
            m_ = np.eye(na - 1)[None] - np.einsum('sai,sib->sab', Kr, Gr)
            rhs = np.einsum('sai,si->sa', Kr, g0)[..., None]
            ar = np.linalg.solve(m_, rhs)[..., 0]       # (N, na-1)
            z = np.einsum('si,si->s', k0, g0 + np.einsum('sib,sb->si', Gr, ar))
        return -k * z


# ====================================================================== analysis
def _loop_freq(L, w):
    return L(1j * np.asarray(w, float))


def disk_margin(S):
    """Symmetric disk margin from samples of S = 1/(1+L):  alpha = 1 / max|S - 1/2|.
    The loop stays stable for every simultaneous gain and phase change f in the disk
    f = (1 + alpha d/2)/(1 - alpha d/2), |d| < 1:  gain within [1/g, g], g = (2+alpha)/(2-alpha),
    and phase within +-2 atan(alpha/2).  Returns (alpha, gain margin dB, phase margin deg, index)."""
    m = np.abs(S - 0.5)
    i = int(np.argmax(m))
    a = float(min(1.0 / m[i], 2.0 - 1e-9)) if m[i] > 0 else 2.0 - 1e-9
    return a, 20 * math.log10((2 + a) / (2 - a)), math.degrees(2 * math.atan(a / 2)), i


def loop_analysis(cm: ControllerModel, wmin=1e-3, wmax=40.0, npts=1500, tau=0.0):
    """tau > 0: a pure delay exp(-s tau) in the loop (controller time step and hold).  Stability is
    then taken from the Nyquist count Z = N + P, because the delayed loop has no finite A matrix."""
    Lss = cm.loop_system()
    if tau > 0:
        def L(s, _L=cm.loop_eval):
            s = np.atleast_1d(np.asarray(s, complex))
            return _L(s) * np.exp(-s * tau)
    else:
        L = cm.loop_eval
    lam_ol = linalg.eigvals(Lss.A)
    w = np.unique(np.concatenate([np.logspace(np.log10(wmin), np.log10(wmax), npts),
                                  *[abs(l) * (1 + np.linspace(-0.05, 0.05, 25)) for l in lam_ol
                                    if 0 < abs(l) < wmax and abs(l.real) < 0.1 * abs(l)]]))
    w = w[(w >= wmin) & (w <= wmax)]
    Lw = _loop_freq(L, w)
    f = lambda x: complex(_loop_freq(L, np.array([x]))[0])
    # ---- gain margins: Im L = 0, Re L < 0
    gms = []
    im = Lw.imag
    for i in np.where(np.sign(im[:-1]) * np.sign(im[1:]) < 0)[0]:
        try:
            wc = optimize.brentq(lambda x: f(x).imag, w[i], w[i + 1], xtol=1e-12)
        except ValueError:
            continue
        v = f(wc)
        if v.real < 0:
            gms.append((wc, -1.0 / v.real))
    # ---- phase margins: |L| = 1
    pms = []
    lm = np.abs(Lw) - 1
    for i in np.where(np.sign(lm[:-1]) * np.sign(lm[1:]) < 0)[0]:
        try:
            wc = optimize.brentq(lambda x: abs(f(x)) - 1, w[i], w[i + 1], xtol=1e-12)
        except ValueError:
            continue
        v = f(wc)
        ph = math.degrees(math.atan2(v.imag, v.real))       # (-180, 180]
        pm = 180.0 + ph if ph <= 0 else ph - 180.0
        pms.append((wc, pm, v))
    # ---- sensitivity
    S = 1.0 / (1.0 + Lw)
    T = Lw / (1.0 + Lw)
    iS = int(np.argmax(np.abs(S)))
    iT = int(np.argmax(np.abs(T)))
    dist = np.abs(1 + Lw)
    iD = int(np.argmin(dist))
    # refine s_m
    try:
        r = optimize.minimize_scalar(lambda x: abs(1 + f(x)), bracket=(w[max(iD - 1, 0)], w[iD], w[min(iD + 1, len(w) - 1)]))
        sm, wsm = float(r.fun), float(r.x)
    except Exception:
        sm, wsm = float(dist[iD]), float(w[iD])
    # bandwidth of T (first -3 dB crossing from low frequency)
    Tm = np.abs(T)
    bw = None
    lowT = Tm[0]
    idx = np.where(Tm < lowT / math.sqrt(2))[0]
    if idx.size and lowT > 0.5:
        bw = float(w[idx[0]])
    # ---- stability via eigenvalues and Nyquist bookkeeping
    Acl = cm.closed_loop()[0]
    lam_cl = linalg.eigvals(Acl)
    tol = 1e-7
    P = int(np.sum(lam_ol.real > tol))
    N = nyquist_winding(L, lam_ol)
    Z = int(np.sum(lam_cl.real > tol)) if tau <= 0 else max(0, N + P)
    alpha, dgm_db, dpm, iA = disk_margin(S)
    L = Lss
    gm_list = [(a, b, 20 * math.log10(b)) for a, b in gms if b > 0]
    gm_min = min(gm_list, key=lambda t: abs(t[2])) if gm_list else None
    pm_min = min(pms, key=lambda t: abs(t[1])) if pms else None
    dm = None
    if pms:
        dms = [(math.radians(p) / wc) for wc, p, _ in pms if p > 0]
        dm = min(dms) if dms else 0.0
    return dict(
        w=w, L=Lw, S=S, T=T,
        gm=[dict(w=a, gm=b, gm_db=c) for a, b, c in gm_list],
        pm=[dict(w=a, pm=b, re=c.real, im=c.imag) for a, b, c in pms],
        gm_min=None if gm_min is None else dict(w=gm_min[0], gm=gm_min[1], gm_db=gm_min[2]),
        pm_min=None if pm_min is None else dict(w=pm_min[0], pm=pm_min[1]),
        delay_margin=dm, s_m=sm, w_sm=wsm, Smax=float(np.abs(S[iS])), w_Smax=float(w[iS]),
        Tmax=float(np.abs(T[iT])), w_Tmax=float(w[iT]), bw_T=bw,
        P=P, Z=Z, N_cw=N, nyquist_ok=(Z == P + N), stable=(Z == 0),
        disk_alpha=alpha, disk_gm_db=dgm_db, disk_pm=dpm, w_disk=float(w[iA]), tau=tau,
        lam_ol=lam_ol, lam_cl=lam_cl)


def nyquist_winding(L, lam_ol, wmax=1e3):
    """Clockwise encirclements of -1 by L along the standard Nyquist contour
    (indented to the right around poles on the imaginary axis)."""
    axis = [l for l in lam_ol if abs(l.real) <= 1e-7 * max(1.0, abs(l))]
    eps = 1e-6
    w = np.logspace(-6, np.log10(wmax), 6000)
    # add points around lightly damped poles
    extra = [abs(l.imag) * (1 + np.linspace(-0.02, 0.02, 61)) for l in lam_ol if abs(l.imag) > 0 and abs(l.real) < 0.05 * abs(l)]
    if extra:
        w = np.unique(np.concatenate([w, *extra]))
    w = w[w > 0]

    Lof = L
    # path: -j*wmax -> -j*eps, semicircle to +j*eps through the right, +j*eps -> +j*wmax
    s_neg = -1j * w[::-1]
    s_pos = 1j * w
    th = np.linspace(-math.pi / 2, math.pi / 2, 181)
    s_arc = eps * np.exp(1j * th) if any(abs(l) < 1e-7 for l in axis) else np.array([])
    s = np.concatenate([s_neg, s_arc, s_pos])
    # axis poles away from the origin: shift the contour slightly right of them
    for l in axis:
        if abs(l) >= 1e-7:
            s = np.where(np.abs(s - l) < 1e-6, s + 1e-6, s)
    Ls = Lof(s)
    ang = np.unwrap(np.angle(1 + Ls))
    turns = (ang[-1] - ang[0]) / (2 * math.pi)
    # the large semicircle (s -> infinity) adds nothing for a proper L
    return int(round(-turns))    # contour runs clockwise around the RHP


def root_locus(cm: ControllerModel, ks=None, fmax_hz=1.0):
    if ks is None:
        ks = np.concatenate([np.linspace(0.0, 1.0, 26), np.linspace(1.04, 3.0, 50)])
    out = []
    for k in ks:
        A = cm.closed_loop(k)[0]
        lam = linalg.eigvals(A)
        lam = lam[(np.abs(lam) / (2 * math.pi) <= fmax_hz) & (lam.imag >= -1e-9)]
        out.append(dict(k=float(k), re=lam.real.tolist(), im=lam.imag.tolist()))
    return out


def label_cl_poles(cm: ControllerModel, fmax_hz=None):
    """Closed-loop poles with participation-based labels (plant DOF or controller block)."""
    A, _, _, _, states = cm.closed_loop()
    lam, V = linalg.eig(A)
    try:
        W = np.linalg.inv(V)
    except np.linalg.LinAlgError:
        W = np.linalg.pinv(V)
    ol = {round(m['re'], 6) + 1j * round(m['im'], 6): m for m in analyse_modes(cm.om)}
    names = [s.split(':', 1)[1] if s.startswith('plant:') else s for s in states]
    res = []
    for k, l in enumerate(lam):
        if l.imag < -1e-9:
            continue
        if fmax_hz is not None and abs(l) / (2 * math.pi) > fmax_hz:
            continue
        p = np.abs(W[k, :] * V[:, k])
        p = p / (p.sum() or 1)
        g = {}
        for nm, pv in zip(names, p):
            g[nm] = g.get(nm, 0.0) + pv
        top = sorted(g.items(), key=lambda kv: -kv[1])[:4]
        ctrl_share = sum(pv for s, pv in zip(states, p) if not s.startswith('plant:'))
        wn = abs(l)
        z = -l.real / wn if wn > 1e-9 else 0.0
        res.append(dict(at_origin=bool(wn <= 1e-9), re=float(l.real), im=float(l.imag), fn=wn / (2 * math.pi), wn=wn, zeta=float(z),
                        label=top[0][0], top=[[a, float(b)] for a, b in top],
                        controller_share=float(ctrl_share), origin='controller' if ctrl_share > 0.5 else 'plant'))
    res.sort(key=lambda r: r['fn'])
    return res


def cl_modes(cm: ControllerModel, fmax_hz=3.0, max_zeta=0.9):
    """Oscillatory closed-loop modes in the form modal.track_modes expects.

    A mode mostly made of plant states inherits label / family / symmetry from the open-loop mode
    with the most similar structural shape (MAC, with a penalty on frequency distance); a mode
    mostly made of controller states is named after its controller block (family 'controller').
    The shape used for tracking is the plant structural part of the eigenvector."""
    om = cm.om
    ol = [m for m in analyse_modes(om) if m['oscillatory']]
    sp = om._struct_pos
    A, _, _, _, states = cm.closed_loop()
    lam, V = linalg.eig(A)
    try:
        W = np.linalg.inv(V)
    except np.linalg.LinAlgError:
        W = np.linalg.pinv(V)
    is_ctrl = np.array([not s.startswith('plant:') for s in states])
    out = []
    for k, l in enumerate(lam):
        wn = abs(l)
        if l.imag <= 1e-9 or wn / (2 * math.pi) > fmax_hz:
            continue
        z = -l.real / wn
        if z >= max_zeta:
            continue
        p = np.abs(W[k, :] * V[:, k])
        p = p / (p.sum() or 1.0)
        share = float(p[is_ctrl].sum())
        shape = V[sp, k]
        nrm = np.linalg.norm(shape)
        shape = shape / nrm if nrm > 0 else shape
        fn = wn / (2 * math.pi)
        if share > 0.5 or not ol:
            j = int(np.argmax(np.where(is_ctrl, p, -1.0)))
            lab, fam, sym, whirl = states[j], 'controller', '', 0.0
        else:
            def score(m):
                mac = abs(np.vdot(m['_shape'], shape)) ** 2 if m['_shape'].shape == shape.shape else 0.0
                return mac - 0.5 * abs(math.log(max(fn, 1e-6) / max(m['fn'], 1e-6)))
            m = max(ol, key=score)
            lab, fam, sym, whirl = m['label'], m['family'], m['sym'], m['whirl']
        out.append(dict(fn=fn, fd=abs(l.imag) / (2 * math.pi), zeta=float(z), label=lab, family=fam, sym=sym,
                        whirl=whirl, oscillatory=True, controller_share=share, _shape=shape))
    return out


def controller_bode(cm: ControllerModel, w):
    """Frequency response of the controller from gen speed (rad/s) to actuator command."""
    K = cm.K
    H = lti.freqresp(K.A, K.B, K.C, K.D, w)[:, 0, 0]
    return H
