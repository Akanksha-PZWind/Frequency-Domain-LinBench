"""Eigen-analysis, mode identification, whirl classification and Campbell tracking.

Mode identification uses participation factors p_ik = |W_ki V_ik| (W = V^-1), summed per
physical DOF (position + velocity states of the same DOF, and hydrodynamic states of the
same platform DOF).  For rotor modes the MBC components tell the symmetry:
  collective  -> symmetric (all blades in phase)
  cos/sin     -> asymmetric; with q_c = Re(C e^{jwt}), q_s = Re(S e^{jwt}), w > 0:
                 forward whirl  <=> |C + jS| >> |C - jS|
                 backward whirl <=> |C - jS| >> |C + jS|
(A pattern cos(psi_b - w t) travels with the rotor: C = 1, S = -j.)
"""
from __future__ import annotations

import math
from collections import Counter, defaultdict

import numpy as np
from scipy import linalg
from scipy.optimize import linear_sum_assignment

ROTOR_FAMS = ('blade flap', 'blade edge', 'blade torsion')
NONSTRUCT = ('hydro/aero states',)


def _left_right(A):
    lam, V = linalg.eig(A)
    try:
        cond = np.linalg.cond(V)
    except Exception:
        cond = np.inf
    if np.isfinite(cond) and cond < 1e13:
        W = np.linalg.inv(V)
    else:
        _, VL = linalg.eig(A, left=True, right=False)
        W = VL.conj().T
        s = np.sum(W * V.T, axis=1)
        W = W / s[:, None]
    return lam, V, W


def analyse_modes(om, keep_real=True):
    """Return list of mode dicts for one OpModel (one entry per conjugate pair)."""
    if om._eig is not None:
        return om._eig
    lam, V, W = _left_right(om.A)
    n = len(lam)
    dofs = om.x_dof
    names = [d[0] for d in dofs]
    fams = [d[1] for d in dofs]
    comps = [d[2] for d in dofs]
    # rotor triplets in the fixed frame (position states and matching velocities)
    trips = [(t, 'pos') for t in om.plan.x_pos2] + [(t, 'first') for t in om.plan.x_first]
    vel_of = {tuple(p): v for v, p in om.plan.x_vel2}
    struct_pos = [i for i, ch in enumerate(om.x) if ch.deriv_order == 2 and fams[i] not in NONSTRUCT
                  and not ch.name.startswith('First time derivative')]
    struct_pos = [i for i in struct_pos if i not in {v for v, _ in []}]
    vel_idx = {v for vv, _ in om.plan.x_vel2 for v in vv}
    struct_pos = [i for i in struct_pos if i not in vel_idx]
    if not struct_pos:
        struct_pos = list(range(n))
    modes = []
    for k in range(n):
        if lam[k].imag < -1e-9:
            continue
        if not keep_real and abs(lam[k].imag) <= 1e-9:
            continue
        p = np.abs(W[k, :] * V[:, k])
        tot = p.sum()
        p = p / tot if tot > 0 else p
        g = defaultdict(float)
        fam = defaultdict(float)
        comp = defaultdict(float)
        for i in range(n):
            g[names[i]] += p[i]
            fam[fams[i]] += p[i]
            if fams[i] in ROTOR_FAMS:
                comp['collective' if comps[i] == 'col' else ('cyclic' if comps[i] in ('cos', 'sin') else 'rotating')] += p[i]
        top = sorted(g.items(), key=lambda kv: -kv[1])
        # whirl
        whirl, wsum = 0.0, 0.0
        if lam[k].imag > 1e-9:
            for t, _ in trips:
                wgt = p[t[1]] + p[t[2]]
                v = vel_of.get(tuple(t))
                if v is not None:
                    wgt += p[v[1]] + p[v[2]]
                if wgt < 1e-4:
                    continue
                C, S = V[t[1], k], V[t[2], k]
                fw, bw = abs(C + 1j * S), abs(C - 1j * S)
                if fw + bw > 0:
                    whirl += wgt * (fw - bw) / (fw + bw)
                    wsum += wgt
        whirl = whirl / wsum if wsum > 0 else 0.0
        rotor_share = sum(fam[f] for f in ROTOR_FAMS)
        dom_name, dom_share = top[0]
        dom_fam = next(fams[i] for i in range(n) if names[i] == dom_name)
        lamk = lam[k]
        wn = abs(lamk)
        zeta = -lamk.real / wn if wn > 0 else 1.0
        sym = ''
        if dom_fam in ROTOR_FAMS or rotor_share > 0.5:
            if comp['collective'] >= comp['cyclic']:
                sym = 'collective'
            elif abs(lamk.imag) > 1e-9:
                sym = 'FW' if whirl > 0.25 else ('BW' if whirl < -0.25 else 'cyclic')
            else:
                sym = 'cyclic'
        if wn < 1e-7:
            label = 'Rotor azimuth (rigid body)' if 'azimuth' in dom_name.lower() else f'{dom_name} (rigid body)'
        else:
            label = dom_name
            if sym == 'collective':
                label += ' · collective'
            elif sym in ('FW', 'BW'):
                label += f' · {sym} whirl'
            elif sym == 'cyclic':
                label += ' · cyclic'
            # secondary rotor involvement on a support-structure mode
            if dom_fam not in ROTOR_FAMS and 0.15 < rotor_share <= 0.5 and abs(lamk.imag) > 1e-9:
                tag = 'collective' if comp['collective'] >= comp['cyclic'] else (
                    'FW' if whirl > 0.25 else ('BW' if whirl < -0.25 else 'cyclic'))
                label += f' (+ rotor {tag} {rotor_share * 100:.0f}%)'
        shape = V[struct_pos, k]
        nrm = np.linalg.norm(shape)
        shape = shape / nrm if nrm > 0 else shape
        modes.append(dict(
            k=int(k), re=float(lamk.real), im=float(lamk.imag), fn=wn / (2 * math.pi),
            fd=abs(lamk.imag) / (2 * math.pi), wn=wn, zeta=float(zeta), label=label,
            dof=dom_name, family=dom_fam, share=float(dom_share), sym=sym, whirl=float(whirl),
            rotor_share=float(rotor_share), oscillatory=bool(abs(lamk.imag) > 1e-9),
            top=[[a, float(b)] for a, b in top[:6]],
            families={a: float(b) for a, b in fam.items() if b > 1e-3},
            _shape=shape))
    modes.sort(key=lambda m: (m['fn'], m['k']))
    om._eig = modes
    om._eig_raw = (lam, V, W)
    om._struct_pos = struct_pos
    return modes


def mode_table(modes):
    return [{k: v for k, v in m.items() if not k.startswith('_')} for m in modes]


def _mac(a, b):
    if a.shape != b.shape:
        return 0.0
    num = abs(np.vdot(a, b)) ** 2
    den = (np.vdot(a, a).real * np.vdot(b, b).real) or 1.0
    return float(num / den)


def campbell(models, include_nonstructural=False, fmax=None, max_zeta=0.9):
    """Track modes across operating points (models sorted by wind speed)."""
    per_op = []
    for om in models:
        ms = [m for m in analyse_modes(om) if m['oscillatory'] and m['zeta'] < max_zeta
              and (include_nonstructural or m['family'] not in NONSTRUCT)
              and (fmax is None or m['fn'] <= fmax)]
        per_op.append(ms)
    return track_modes(per_op, models)


def track_modes(per_op, models):
    """Join the mode lists of consecutive operating points into tracks (MAC + optimal assignment).
    per_op[j]: mode dicts with fn, fd, zeta, label, family, sym, whirl, _shape for models[j]."""
    tracks = []
    active = []   # indices into tracks that were extended at the previous op
    for j, ms in enumerate(per_op):
        if j == 0 or not active:
            active = []
            for m in ms:
                tracks.append(dict(points=[(j, m)]))
                active.append(len(tracks) - 1)
            continue
        prev = [tracks[t]['points'][-1][1] for t in active]
        cost = np.zeros((len(prev), len(ms)))
        for a, pm in enumerate(prev):
            for b, m in enumerate(ms):
                mac = _mac(pm['_shape'], m['_shape'])
                df = abs(math.log(max(m['fn'], 1e-6) / max(pm['fn'], 1e-6)))
                fam_pen = 0.0 if pm['family'] == m['family'] else 0.3
                cost[a, b] = (1 - mac) + 0.6 * df + fam_pen
        r, c = linear_sum_assignment(cost) if cost.size else ([], [])
        new_active, used = [], set()
        for a, b in zip(r, c):
            if cost[a, b] < 0.8:
                t = active[a]
                tracks[t]['points'].append((j, ms[b]))
                new_active.append(t)
                used.add(b)
        for b, m in enumerate(ms):
            if b not in used:
                tracks.append(dict(points=[(j, m)]))
                new_active.append(len(tracks) - 1)
        # tracks that were not continued stay closed; allow them no gap bridging
        active = new_active
    out = []
    for t in tracks:
        pts = t['points']
        lab = Counter(p[1]['label'] for p in pts).most_common(1)[0][0]
        fam = Counter(p[1]['family'] for p in pts).most_common(1)[0][0]
        sym = Counter(p[1]['sym'] for p in pts).most_common(1)[0][0]
        out.append(dict(label=lab, family=fam, sym=sym,
                        mean_f=float(np.mean([p[1]['fn'] for p in pts])),
                        points=[dict(op=j, k=m['k'], wind=models[j].wind, fn=m['fn'], fd=m['fd'], zeta=m['zeta'],
                                     label=m['label'], whirl=m['whirl']) for j, m in pts]))
    out.sort(key=lambda d: d['mean_f'])
    # disambiguate identical labels
    seen = Counter()
    for d in out:
        seen[d['label']] += 1
        if seen[d['label']] > 1:
            d['label'] = f"{d['label']} ({seen[d['label']]})"
    harm = [dict(wind=om.wind, f1p=om.omega / (2 * math.pi)) for om in models]
    return dict(tracks=out, rotor=harm)
