"""Controllability, observability and Hankel singular values of an operating-point model.

Rank tests use the orthogonal staircase (Kalman decomposition, Van Dooren 1981) on a
balanced copy of A, never the ill-conditioned matrix [B AB A^2B ...].  The modes left in the
uncontrollable / unobservable block are the eigenvalues of that block.

Per mode k (right/left eigenvectors v_k, w_k) the PBH angle measures of Hamdan & Nayfeh (1989)
    controllability  c_k = |w_k B| / (|w_k| |B|)      observability  o_k = |C v_k| / (|C| |v_k|)
are cosines in [0, 1]; 0 means the PBH rank test fails for that mode.  They depend on the
state coordinates.  The modal Hankel value h_k = |C v_k| |w_k B| / (2 |Re lambda_k|) does not,
and approaches the Hankel singular values of the pair for lightly damped, well separated modes.

Hankel singular values are those of the stable part (ordered Schur form + Sylvester
decoupling, as MATLAB hsvd does); modes with Re lambda >= -tol are reported separately.
Input and output channels are in display units (deg, deg/s, ...), so the HSVs depend on them.
"""
from __future__ import annotations

import math

import numpy as np
from scipy import linalg

from .model import unit_convert


def _scaled(om, ins, outs):
    """B, C, D restricted to the chosen channels and scaled to display units."""
    fin = np.array([unit_convert(om.u[i].unit)[1] for i in ins])
    fout = np.array([unit_convert(om.y[i].unit)[1] for i in outs])
    B = om.B[:, ins] / fin[None, :]
    C = om.C[outs, :] * fout[:, None]
    D = om.D[np.ix_(outs, ins)] * fout[:, None] / fin[None, :]
    return B, C, D


def staircase(A, B, tol):
    """Orthogonal staircase.  Returns (nc, Z, At) with At = Z' A Z; the first nc coordinates
    span the controllable subspace and At[nc:, :nc] = 0."""
    n = A.shape[0]
    Z = np.eye(n)
    At = A.copy()
    Bk = B.copy()
    nc = 0
    while nc < n and Bk.size:
        U, s, _ = linalg.svd(Bk, full_matrices=True)
        r = int((s > tol).sum())
        if r == 0:
            break
        T = np.eye(n)
        T[nc:, nc:] = U
        At = T.T @ At @ T
        Z = Z @ T
        Bk = At[nc + r:, nc:nc + r]
        nc += r
    return nc, Z, At


def kalman(A, B, C, rtol=1e-9):
    """Ranks and the uncontrollable / unobservable eigenvalues."""
    n = A.shape[0]
    # diagonal similarity: equalises row/column norms without changing either rank
    Ab, (sc, _) = linalg.matrix_balance(A, permute=False, separate=True)
    Bb, Cb = B / sc[:, None], C * sc[None, :]
    nb = np.linalg.norm(Bb, axis=0)
    no = np.linalg.norm(Cb, axis=1)
    Bb = Bb[:, nb > 0] / nb[nb > 0]           # column / row scaling does not change the ranks
    Cb = Cb[no > 0] / no[no > 0, None]
    tol = rtol * max(1.0, np.linalg.norm(Ab, 2))
    nc, Zc, Atc = staircase(Ab, Bb, tol)
    no_, Zo, Ato = staircase(Ab.T, Cb.T, tol)
    unc = linalg.eigvals(Atc[nc:, nc:]) if nc < n else np.zeros(0, complex)
    uno = linalg.eigvals(Ato[no_:, no_:]) if no_ < n else np.zeros(0, complex)
    # minimal order: observable part of the controllable part
    Ac, Cc = Atc[:nc, :nc], (Cb @ Zc)[:, :nc]
    nmin = staircase(Ac.T, Cc.T, tol)[0] if nc else 0
    return dict(n=n, rank_c=nc, rank_o=no_, n_min=nmin, uncontrollable=unc, unobservable=uno, tol=tol)


def stable_part(A, B, C, tol=1e-8):
    """(As, Bs, Cs, unstable eigenvalues) of the additive stable/antistable split."""
    n = A.shape[0]
    T, Z, sdim = linalg.schur(A, output='real', sort=lambda re, im: re < -tol)
    Bt, Ct = Z.T @ B, C @ Z
    A11, A12, A22 = T[:sdim, :sdim], T[:sdim, sdim:], T[sdim:, sdim:]
    unst = linalg.eigvals(A22) if sdim < n else np.zeros(0, complex)
    if sdim == 0:
        return np.zeros((0, 0)), np.zeros((0, B.shape[1])), np.zeros((C.shape[0], 0)), unst
    if sdim < n:
        X = linalg.solve_sylvester(A11, -A22, -A12)   # A11 X - X A22 + A12 = 0
        Bs = Bt[:sdim] - X @ Bt[sdim:]
    else:
        Bs = Bt
    return A11, Bs, Ct[:, :sdim], unst


def _factor(P):
    """L with L L' = P for a symmetric positive semi-definite P."""
    s, U = linalg.eigh((P + P.T) / 2)
    return U * np.sqrt(np.clip(s, 0.0, None))[None, :]


def hankel_sv(A, B, C, tol=1e-8):
    As, Bs, Cs, unst = stable_part(A, B, C, tol)
    if As.shape[0] == 0:
        return np.zeros(0), unst
    Wc = linalg.solve_continuous_lyapunov(As, -Bs @ Bs.T)
    Wo = linalg.solve_continuous_lyapunov(As.T, -Cs.T @ Cs)
    hsv = linalg.svd(_factor(Wo).T @ _factor(Wc), compute_uv=False)
    return np.sort(hsv)[::-1], unst


def modal_measures(lam, V, W, B, C):
    """PBH angle measures and modal Hankel value per eigenvalue."""
    wB = W @ B                                  # rows: w_k B
    Cv = C @ V                                  # columns: C v_k
    nB, nC = np.linalg.norm(B, 2) or 1.0, np.linalg.norm(C, 2) or 1.0
    wn = np.linalg.norm(W, axis=1)
    vn = np.linalg.norm(V, axis=0)
    wb = np.linalg.norm(wB, axis=1)
    cv = np.linalg.norm(Cv, axis=0)
    ctrb = wb / (wn * nB)
    obsv = cv / (vn * nC)
    with np.errstate(divide='ignore'):
        h = np.where(np.abs(lam.real) > 1e-12, wb * cv / (2 * np.abs(lam.real)), np.inf)
    return ctrb, obsv, h


def _match(eigs, lam, modes_by_k):
    """Label each eigenvalue in eigs with the nearest mode of the full model."""
    out = []
    for e in eigs:
        if e.imag < -1e-9:
            continue
        k = int(np.argmin(np.abs(lam - e)))
        kk = k if lam[k].imag >= -1e-9 else int(np.argmin(np.abs(lam - np.conj(lam[k]))))
        m = modes_by_k.get(kk)
        wn = abs(e)
        out.append(dict(re=float(e.real), im=float(abs(e.imag)), fn=wn / (2 * math.pi),
                        zeta=float(-e.real / wn) if wn > 0 else 1.0, label=m['label'] if m else ''))
    return sorted(out, key=lambda d: d['fn'])


def analyse(om, modes, ins, outs, tol=1e-7):
    """Full report for the channel sets ins -> outs of one OpModel (modes: analyse_modes(om))."""
    lam, V, W = om._eig_raw
    B, C, D = _scaled(om, ins, outs)
    kal = kalman(om.A, B, C)
    hsv, unst = hankel_sv(om.A, B, C)
    ctrb, obsv, h = modal_measures(lam, V, W, B, C)
    by_k = {m['k']: m for m in modes}
    rows = []
    for m in modes:
        k = m['k']
        rows.append(dict(k=k, label=m['label'], family=m['family'], sym=m['sym'], re=m['re'], im=m['im'],
                         fn=m['fn'], zeta=m['zeta'], ctrb=float(ctrb[k]), obsv=float(obsv[k]),
                         hankel=float(h[k]) if np.isfinite(h[k]) else None,
                         weak_c=bool(ctrb[k] < tol), weak_o=bool(obsv[k] < tol)))
    tail = np.concatenate([np.cumsum(hsv[::-1])[::-1][1:], [0.0]]) if hsv.size else np.zeros(0)
    bound = 2 * tail                            # balanced-truncation H-inf error bound, order r = index + 1
    r1 = next((i + 1 for i, b in enumerate(bound) if b <= 0.01 * hsv[0]), None) if hsv.size else None
    unc, uno = kal['uncontrollable'], kal['unobservable']
    return dict(
        n=kal['n'], rank_c=kal['rank_c'], rank_o=kal['rank_o'], n_min=kal['n_min'],
        n_inputs=len(ins), n_outputs=len(outs),
        stabilizable=bool(np.all(unc.real < -1e-9)), detectable=bool(np.all(uno.real < -1e-9)),
        uncontrollable=_match(unc, lam, by_k), unobservable=_match(uno, lam, by_k),
        hsv=hsv, bound=bound, order_1pct=r1, n_unstable=int(len(unst)),
        unstable=[dict(re=float(e.real), im=float(e.imag)) for e in unst if e.imag >= -1e-9],
        modes=rows, tol=tol,
        inputs=[dict(i=i, label=om.u_label[i], unit=unit_convert(om.u[i].unit)[0]) for i in ins],
        outputs=[dict(i=i, label=om.y_label[i], unit=unit_convert(om.y[i].unit)[0]) for i in outs])
