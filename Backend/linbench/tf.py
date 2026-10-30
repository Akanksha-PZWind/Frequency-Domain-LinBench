"""Single-input single-output analysis of an operating-point model.

G(s) = c (sI - A)^-1 b + d = sum_k r_k / (s - lambda_k) + d,   r_k = (c v_k)(w_k b)
A mode with |r_k| ~ 0 is not excited by the input or not seen by the output: in the
transfer function its pole is cancelled by a zero at the same place.  Those pairs are
reported separately so the 'extra zeros' on the pole-zero map are explained.
"""
from __future__ import annotations

import math

import numpy as np
from scipy import linalg

from .lti import freqresp, modal_freqresp, step_impulse
from .modal import analyse_modes
from .model import unit_convert


def freq_grid(poles, wmin=1e-3, wmax=40.0, n=700):
    w = list(np.logspace(np.log10(wmin), np.log10(wmax), n))
    for p in poles:
        wn = abs(p)
        if wn <= 0 or not (wmin < wn < wmax):
            continue
        z = max(-p.real / wn, 1e-4)
        if z < 0.3:
            bw = max(4 * z, 0.01)
            w += list(wn * (1 + np.linspace(-bw, bw, 41)))
    w = np.unique(np.clip(np.array(w), wmin, wmax))
    return w


def zeros_of(A, b, c, d):
    n = A.shape[0]
    if n == 0:
        return np.array([])
    M = np.zeros((n + 1, n + 1), complex)
    M[:n, :n] = A
    M[:n, n] = b
    M[n, :n] = c
    M[n, n] = d
    N = np.zeros((n + 1, n + 1))
    N[:n, :n] = np.eye(n)
    z = linalg.eigvals(M, N)
    z = z[np.isfinite(z)]
    return z[np.abs(z) < 1e8]


class SISO:
    def __init__(self, om, i_in, i_out, extra_out=None):
        """extra_out: optional (c_row, d_row, label, unit) replacing a plant output."""
        self.om = om
        modes = analyse_modes(om)
        lam, V, W = om._eig_raw
        self.lam = lam
        self.b = om.B[:, i_in].astype(float)
        if extra_out is None:
            self.c = om.C[i_out, :].astype(float)
            self.d = float(om.D[i_out, i_in])
            ounit = om.y[i_out].unit
            self.out_label = om.y_label[i_out]
        else:
            self.c, drow, self.out_label, ounit = extra_out
            self.d = float(drow[i_in])
        self.in_label = om.u_label[i_in]
        self.in_unit, self.fin = unit_convert(om.u[i_in].unit)
        self.out_unit, self.fout = unit_convert(ounit)
        # display scaling: output in display units per input display unit
        self.scale = self.fout / self.fin
        cv = self.c @ V
        wb = W @ self.b
        self.res = cv * wb
        self.modes = modes
        self._V, self._W = V, W

    # ---------------------------------------------------------------- poles
    def pole_info(self):
        lam, r = self.lam, self.res
        contrib = np.abs(r) / np.maximum(np.abs(lam.real), 1e-9 + 1e-6 * np.abs(lam))
        contrib = np.where(np.abs(lam) < 1e-9, np.where(np.abs(r) > 1e-12, np.inf, 0.0), contrib)
        finite = contrib[np.isfinite(contrib)]
        ref = max(finite.max() if finite.size else 0.0, 1e-300)
        rel = np.where(np.isfinite(contrib), contrib / ref, 1.0)
        # map raw eigen index -> mode label (conjugates share the label)
        lab = {}
        for m in self.modes:
            lab[m['k']] = m
        info = []
        for k, l in enumerate(lam):
            m = lab.get(k)
            if m is None:   # conjugate partner
                kk = int(np.argmin(np.abs(lam - np.conj(l))))
                m = lab.get(kk)
            info.append(dict(re=float(l.real), im=float(l.imag), rel=float(rel[k]),
                             label=m['label'] if m else '', zeta=m['zeta'] if m else None,
                             fn=abs(l) / (2 * math.pi)))
        return info, rel

    # ---------------------------------------------------------------- response
    def response(self, w):
        try:
            condV = np.linalg.cond(self._V)
        except Exception:
            condV = np.inf
        if condV < 1e9:
            G = modal_freqresp(self.lam, self.res, self.d, w)
        else:
            G = freqresp(self.om.A, self.b[:, None], self.c[None, :], np.array([[self.d]]), w)[:, 0, 0]
        return G * self.scale

    def analyse(self, wmin=1e-3, wmax=40.0, sig_tol=1e-6):
        info, rel = self.pole_info()
        lam = self.lam
        keep = rel > sig_tol
        w = freq_grid(lam[keep], wmin, wmax)
        G = self.response(w)
        # zeros: of the minimal (significant-mode) modal realisation
        Lk, bk, ck = lam[keep], (self._W @ self.b)[keep], (self.c @ self._V)[keep]
        zmin = zeros_of(np.diag(Lk), bk, ck, self.d) if Lk.size else np.array([])
        zmin = zmin[np.abs(zmin) < 1e4]
        zfull = zeros_of(self.om.A.astype(complex), self.b, self.c, self.d)
        cancel = 0
        for z in zfull:
            if np.min(np.abs(lam[~keep] - z) / np.maximum(1.0, np.abs(z))) < 1e-4 if (~keep).any() else False:
                cancel += 1
        mag = np.abs(G)
        integ = bool(np.any((np.abs(lam) < 1e-7) & keep))
        nz = np.abs(lam) >= 1e-7
        dc = None if integ else float(((-np.sum(self.res[nz] / lam[nz])).real + self.d) * self.scale)
        ipk = int(np.argmax(mag))
        bw = None
        if dc not in (None, 0.0):
            thr = abs(dc) / math.sqrt(2)
            below = np.where(mag < thr)[0]
            if below.size:
                bw = float(w[below[0]])
        rhp_z = sorted([z for z in zmin if z.real > 1e-8 and abs(z) < 1e3], key=abs)
        rhp_p = [l for l in lam[keep] if l.real > 1e-9]
        # most significant oscillatory mode
        osc = [(rel[k], k) for k in range(len(lam)) if lam[k].imag > 1e-9 and keep[k]]
        dom = info[max(osc)[1]] if osc else None
        ph = np.degrees(np.unwrap(np.angle(G)))
        # relative degree (Markov parameters)
        rd = 0 if abs(self.d) > 1e-14 else None
        if rd is None:
            v = self.b.copy()
            for kdeg in range(1, 6):
                if abs(self.c @ v) > 1e-10 * (np.linalg.norm(self.c) * np.linalg.norm(v) + 1e-300):
                    rd = kdeg
                    break
                v = self.om.A @ v
        return dict(
            w=w.tolist(), mag=mag.tolist(), phase=ph.tolist(),
            re=G.real.tolist(), im=G.imag.tolist(),
            poles=[p for p, kk in zip(info, keep) if kk],
            poles_cancelled=[p for p, kk in zip(info, keep) if not kk],
            zeros=[dict(re=float(z.real), im=float(z.imag)) for z in zmin],
            metrics=dict(
                dc_gain=dc, integrator=integ, peak_gain=float(mag[ipk]), peak_w=float(w[ipk]),
                bandwidth_w=bw, n_poles=int(keep.sum()), n_zeros=int(len(zmin)),
                n_cancelled=int((~keep).sum()), n_rhp_zeros=len(rhp_z), n_rhp_poles=len(rhp_p),
                rhp_zeros=[dict(re=float(z.real), im=float(z.imag)) for z in rhp_z[:4]],
                dominant=dom, rel_degree=rd, full_order=int(len(lam)), full_zeros=int(len(zfull)),
                cancelled_pairs=int(cancel)),
            units=dict(input=self.in_unit, output=self.out_unit, gain=f'{self.out_unit}/{self.in_unit}'),
            labels=dict(input=self.in_label, output=self.out_label))

    def time(self, kind='step', amp=1.0, T=None, npts=1500):
        """amp in input display units; output in display units."""
        info, rel = self.pole_info()
        if T is None:
            sig = [l for l, r in zip(self.lam, rel) if r > 1e-4 and abs(l.real) > 1e-6]
            slow = min((abs(l.real) for l in sig), default=0.05)
            T = float(np.clip(5.0 / slow, 20.0, 600.0))
        t, y = step_impulse(self.om.A, self.b, self.c, self.d, kind, amp / self.fin, T, npts)
        y = y * self.fout
        return t, y, T


def step_metrics(t, y, amp_kind='step'):
    t, y = np.asarray(t), np.asarray(y)
    out = {}
    ipk = int(np.argmax(np.abs(y)))
    peak = float(y[ipk])
    yf = float(np.mean(y[-max(5, len(y) // 50):]))
    tail = y[-max(5, len(y) // 5):]
    band = 0.02 * max(abs(yf), 0.05 * abs(peak), 1e-15)
    settled = bool(np.ptp(tail) <= 2 * band)
    out['final'] = yf if settled else None
    out['peak'] = peak
    out['t_peak'] = float(t[ipk])
    out.update(overshoot_pct=None, undershoot_pct=None, t_rise=None, t_settle=None)
    if amp_kind == 'step' and settled:
        outside = np.where(np.abs(y - yf) > band)[0]
        out['t_settle'] = float(t[outside[-1]]) if outside.size else 0.0
        if abs(yf) > 0.05 * abs(peak):
            s = y / yf
            out['overshoot_pct'] = float(max(0.0, (s.max() - 1) * 100))
            out['undershoot_pct'] = float(max(0.0, -s.min() * 100))
            try:
                t10 = t[np.where(s >= 0.1)[0][0]]
                t90 = t[np.where(s >= 0.9)[0][0]]
                out['t_rise'] = float(t90 - t10)
            except IndexError:
                pass
    out['settled'] = settled
    return out
