"""Minimal continuous-time state-space toolkit (numpy only)."""
from __future__ import annotations

import numpy as np
from scipy import linalg


class SS:
    def __init__(self, A, B, C, D, states=None):
        self.A = np.atleast_2d(np.asarray(A, float)) if np.size(A) else np.zeros((0, 0))
        n = self.A.shape[0]
        D = np.atleast_2d(np.asarray(D, float))
        p, m = D.shape
        self.B = np.asarray(B, float).reshape(n, m) if n else np.zeros((0, m))
        self.C = np.asarray(C, float).reshape(p, n) if n else np.zeros((p, 0))
        self.D = D
        self.states = list(states) if states is not None else [''] * n

    @property
    def n(self):
        return self.A.shape[0]

    @property
    def shape(self):
        return self.D.shape

    def __matmul__(self, other):
        """Series: self after other  (y = self(other(u)))."""
        return series(other, self)

    def freq(self, w):
        """Frequency response H(jw) for an array of w (rad/s): shape (len(w), p, m)."""
        return freqresp(self.A, self.B, self.C, self.D, w)


def gain(k):
    return SS(np.zeros((0, 0)), np.zeros((0, 1)), np.zeros((1, 0)), [[k]])


def lpf1(w, name='LPF'):
    return SS([[-w]], [[w]], [[1.0]], [[0.0]], [name])


def lpf2(w, z, name='LPF'):
    return SS([[0, 1], [-w * w, -2 * z * w]], [[0], [w * w]], [[1, 0]], [[0]], [name, name])


def hpf1(w, name='HPF'):
    return SS([[-w]], [[1.0]], [[-w]], [[1.0]], [name])


def notch(w, bn, bd, name='Notch'):
    return SS([[0, 1], [-w * w, -2 * bd * w]], [[0], [1]], [[0, 2 * (bn - bd) * w]], [[1]], [name, name])


def integrator(name='Integrator'):
    return SS([[0.0]], [[1.0]], [[1.0]], [[0.0]], [name])


def pi(kp, ki, name='PI integrator'):
    return SS([[0.0]], [[1.0]], [[ki]], [[kp]], [name])


def series(first: SS, second: SS) -> SS:
    """u -> first -> second -> y."""
    A = np.block([[first.A, np.zeros((first.n, second.n))],
                  [second.B @ first.C, second.A]])
    B = np.vstack([first.B, second.B @ first.D])
    C = np.hstack([second.D @ first.C, second.C])
    D = second.D @ first.D
    return SS(A, B, C, D, first.states + second.states)


def chain(*blocks) -> SS:
    out = blocks[0]
    for b in blocks[1:]:
        out = series(out, b)
    return out


def parallel_sum(a: SS, b: SS) -> SS:
    """Same input dims, outputs summed... used with different inputs via stack_inputs."""
    A = linalg.block_diag(a.A, b.A)
    B = np.vstack([a.B, b.B])
    C = np.hstack([a.C, b.C])
    return SS(A, B, C, a.D + b.D, a.states + b.states)


def append(a: SS, b: SS) -> SS:
    """Block-diagonal (independent inputs and outputs)."""
    A = linalg.block_diag(a.A, b.A)
    B = linalg.block_diag(a.B, b.B)
    C = linalg.block_diag(a.C, b.C)
    D = linalg.block_diag(a.D, b.D)
    return SS(A, B, C, D, a.states + b.states)


def select(sys: SS, in_map=None, out_map=None) -> SS:
    """Pre/post-multiply by static matrices: y' = out_map @ y, u = in_map @ u'."""
    B, D, C = sys.B, sys.D, sys.C
    if in_map is not None:
        B = B @ in_map
        D = D @ in_map
    if out_map is not None:
        C = out_map @ C
        D = out_map @ D
    return SS(sys.A, B, C, D, sys.states)


def freqresp(A, B, C, D, w):
    w = np.asarray(w, float)
    n = A.shape[0]
    p, m = D.shape
    out = np.empty((len(w), p, m), complex)
    if n == 0:
        out[:] = D
        return out
    # Hessenberg form keeps each solve well conditioned; solves are batched over frequency
    H, Q = linalg.hessenberg(A, calc_q=True)
    Bh, Ch = Q.T @ B, C @ Q
    I = np.eye(n)
    chunk = max(1, int(4e6 // (n * n)))        # bounds memory to ~64 MB of complex matrices
    for k0 in range(0, len(w), chunk):
        wk = w[k0:k0 + chunk]
        M = 1j * wk[:, None, None] * I[None] - H[None]
        X = np.linalg.solve(M, np.broadcast_to(Bh, (len(wk),) + Bh.shape))
        out[k0:k0 + chunk] = np.einsum('ij,sjk->sik', Ch, X) + D[None]
    return out


def modal_freqresp(lam, R, d, w):
    """G(jw) = sum_k R_k / (jw - lam_k) + d  for SISO residues R (vectorised)."""
    s = 1j * np.asarray(w, float)[:, None]
    return (R[None, :] / (s - lam[None, :])).sum(axis=1) + d


def step_impulse(A, b, c, d, kind='step', amp=1.0, T=100.0, npts=2000, x0=None):
    """Exact (zero-order-hold) response of x' = A x + b u, y = c x + d u."""
    n = A.shape[0]
    dt = T / npts
    t = np.arange(npts + 1) * dt
    M = np.zeros((n + 1, n + 1))
    M[:n, :n] = A * dt
    M[:n, n] = b * dt
    E = linalg.expm(M)
    Ad, Bd = E[:n, :n], E[:n, n]
    x = np.zeros(n) if x0 is None else x0.astype(float).copy()
    y = np.empty(npts + 1)
    if kind == 'impulse':
        x = b * amp
        for k in range(npts + 1):
            y[k] = c @ x
            x = Ad @ x
    else:
        for k in range(npts + 1):
            y[k] = c @ x + d * amp
            x = Ad @ x + Bd * amp
    return t, y


# ---------------------------------------------------------------------------------------------
# Pole/residue export for the browser: G(s) = d + sum_k r_k / (s - p_k), with only the upper
# half of each conjugate pair sent (p.im > 0 means 'add the conjugate term').  The browser then
# evaluates the response over whatever frequency or time window is on screen.  Every export is
# checked here against the direct state-space result and dropped (None) if it does not match.

def _phi(w, q):
    """Continuous argument of (j w - q) for w along the imaginary axis (no 2*pi jumps)."""
    a, y = -q.real, w - q.imag
    if a > 1e-300:
        return np.arctan(y / a)
    if a < -1e-300:
        return np.pi - np.arctan(y / -a)
    return np.where(y > 0, np.pi / 2, np.where(y < 0, -np.pi / 2, 0.0))


def modal_freq(p, r, d, w):
    """Mirror of the browser evaluation (half representation)."""
    s = 1j * np.asarray(w, float)[:, None]
    pair = (p.imag > 0)[None, :]
    G = r[None, :] / (s - p[None, :]) + np.where(pair, np.conj(r)[None, :] / (s - np.conj(p)[None, :]), 0)
    return G.sum(axis=1) + d


def modal_phase(m, w, G):
    """Unwrapped phase [rad] from the pole-zero form, snapped onto arg G."""
    th = np.full(len(w), m['argk'] - m['off'])
    for z in m['_z']:
        th += _phi(w, z)
    for q in m['_pfull']:
        th -= _phi(w, q)
    g = np.angle(G)
    return g + 2 * np.pi * np.round((th - g) / (2 * np.pi))


def modal_time(p, r, d, t, kind='step', amp=1.0):
    t = np.asarray(t, float)[:, None]
    wgt = np.where(p.imag > 0, 2.0, 1.0)[None, :]
    if kind == 'impulse':
        y = (wgt * (r[None, :] * np.exp(p[None, :] * t)).real).sum(axis=1)
    else:
        small = np.abs(p) < 1e-12
        ps = np.where(small, 1.0, p)
        term = np.where(small[None, :], r[None, :] * t, r[None, :] * np.expm1(ps[None, :] * t) / ps[None, :])
        y = (wgt * term.real).sum(axis=1) + d
    return amp * np.where(t[:, 0] >= 0, y, 0.0)


def modal_export(A, b, c, d, scale=1.0, time=None, rtol=1e-5):
    """Pole/residue/zero form of the SISO system (A, b, c, d) * scale for the browser, or None.

    time: optional (T, kind) to verify the step/impulse response over [0, T] too."""
    A = np.atleast_2d(np.asarray(A, float))
    b, c, d = np.asarray(b, float).ravel(), np.asarray(c, float).ravel(), float(d)
    n = A.shape[0]
    if n == 0:
        return dict(p=[], r=[], z=[], d=d * scale, argk=0.0 if d >= 0 else np.pi, off=0.0, phase_ok=True)
    lam, V = linalg.eig(A)
    try:
        cond = np.linalg.cond(V)
    except Exception:
        return None
    if not np.isfinite(cond) or cond > 1e10:
        return None
    W = np.linalg.inv(V)
    cv, wb = c @ V, W @ b
    res = cv * wb * scale
    contrib = np.abs(res) / np.maximum(np.abs(lam.real), 1e-6 * np.abs(lam) + 1e-12)
    origin = np.abs(lam) < 1e-9                 # integrators: always kept, but not the scale for pruning
    fin = contrib[~origin]
    ref = max(float(fin.max()) if fin.size else 0.0, abs(d * scale), 1e-300)
    keep = (contrib > 1e-13 * ref) | (origin & (np.abs(res) > 0))
    # half representation: real poles, and the upper member of each conjugate pair
    real = keep & (np.abs(lam.imag) <= 1e-9 * np.maximum(1.0, np.abs(lam)))
    up = keep & (lam.imag > 1e-9 * np.maximum(1.0, np.abs(lam)))
    ph = np.concatenate([lam[real].real.astype(complex), lam[up]])
    rh = np.concatenate([res[real].real.astype(complex), res[up]])
    ds = d * scale
    # frequency check against the direct state-space response
    wc = np.logspace(-4, 3, 300)
    wn = np.abs(lam[keep])
    wc = np.unique(np.concatenate([wc] + [x * (1 + np.linspace(-0.05, 0.05, 9)) for x in wn if 1e-4 < x < 1e3]))
    Gd = freqresp(A, b[:, None], c[None, :], np.array([[d]]), wc)[:, 0, 0] * scale
    mx = np.abs(Gd).max()
    natural = abs(scale) * (np.linalg.norm(c) * np.linalg.norm(b) / max(np.abs(lam).min(), 1e-3) + abs(d))
    if mx <= 1e-12 * natural:                   # the channel pair is not connected: G = 0 exactly
        return dict(p=np.zeros(0), r=np.zeros(0), z=np.zeros(0), d=0.0, argk=0.0, off=0.0, phase_ok=True)
    Gm = modal_freq(ph, rh, ds, wc)
    if np.max(np.abs(Gm - Gd) / np.maximum(np.abs(Gd), 1e-6 * mx)) > rtol:
        return None
    out = dict(p=ph, r=rh, d=ds)
    # zeros of the kept modal realisation, gain argument, reference branch at 1e-3 rad/s
    try:
        from .tf import zeros_of
        z = zeros_of(np.diag(lam[keep]), wb[keep], cv[keep], d)
        z = z[np.abs(z) < 1e6]
        pfull = lam[keep]
        s0 = 1j * 1.2345
        G0 = (cv[keep] * wb[keep] / (s0 - pfull)).sum() + d
        argk = float(np.angle(G0) + np.sum(np.angle(s0 - pfull)) - np.sum(np.angle(s0 - z))) if len(z) else \
            float(np.angle(G0) + np.sum(np.angle(s0 - pfull)))
        m = dict(argk=argk, off=0.0, _z=z, _pfull=pfull)
        th_ref = modal_phase(m, np.array([1e-3]), modal_freq(ph, rh, ds, [1e-3]))[0]
        m['off'] = float(2 * np.pi * np.round(th_ref / (2 * np.pi)))
        # the pole-zero phase is continuous by construction; it must agree with arg G up to whole
        # turns wherever G is not vanishingly small (else the zeros or gain are not trustworthy)
        th = np.full(len(wc), argk)
        for zz in z:
            th += _phi(wc, zz)
        for q in pfull:
            th -= _phi(wc, q)
        g = np.angle(Gm)
        dev = np.abs((th - g) - 2 * np.pi * np.round((th - g) / (2 * np.pi)))
        ok = bool(np.all(dev[np.abs(Gm) > 1e-6 * mx] < 0.2))
        out.update(z=z, argk=argk, off=m['off'], phase_ok=ok)
    except Exception:
        out.update(z=np.zeros(0), argk=0.0, off=0.0, phase_ok=False)
    if time is not None:                        # (T, kind): unit-amplitude check, the response is linear
        T, kind = time
        t, y = step_impulse(A, b, c, d, kind, 1.0, T, 600)
        y = y * scale
        ym = modal_time(ph, rh, ds, t, kind, 1.0)
        if np.max(np.abs(ym - y)) > 1e-5 * (np.abs(y).max() or 1.0):
            return None
    return out


class FastSISO:
    """Evaluate a SISO SS at many complex s cheaply: modal residues when the eigenvector
    basis is well conditioned, otherwise a Hessenberg solve per point."""

    def __init__(self, sys: SS, i=0, j=0):
        A, b, c, d = sys.A, sys.B[:, j], sys.C[i, :], float(sys.D[i, j])
        self.A, self.b, self.c, self.d = A, b, c, d
        self.modal = False
        if A.shape[0] == 0:
            self.lam = np.zeros(0)
            self.res = np.zeros(0)
            self.modal = True
            return
        lam, V = linalg.eig(A)
        try:
            cond = np.linalg.cond(V)
        except Exception:
            cond = np.inf
        self.lam = lam
        if cond < 1e10:
            W = np.linalg.inv(V)
            self.res = (c @ V) * (W @ b)
            self.modal = True
        else:
            self.H, Q = linalg.hessenberg(A, calc_q=True)
            self.bh, self.ch = Q.T @ b, c @ Q

    def __call__(self, s):
        s = np.atleast_1d(np.asarray(s, complex))
        if self.modal:
            out = np.empty(len(s), complex)
            for k0 in range(0, len(s), 2000):
                ss = s[k0:k0 + 2000, None]
                out[k0:k0 + 2000] = (self.res[None, :] / (ss - self.lam[None, :])).sum(axis=1) + self.d
            return out
        n = self.A.shape[0]
        I = np.eye(n)
        return np.array([self.ch @ linalg.solve(si * I - self.H, self.bh) + self.d for si in s])
