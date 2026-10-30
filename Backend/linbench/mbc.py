"""Multi-blade coordinate (Coleman) transformation for 3-bladed rotors.

x_rot = T x_nr,  T = [[T1, 0, 0], [Omega*T2, T1, 0], [0, 0, T1]]  (2nd-order and 1st-order triplets)
A_nr = T^-1 (A T - Tdot),  B_nr = T^-1 B T1_u,  C_nr = T1_y^-1 C T,  D_nr = T1_y^-1 D T1_u
Blade b sits at psi_b = psi + 2*pi*(b-1)/3 (psi = azimuth of blade 1, from the file header).
Reference: G. Bir, "Multi-blade coordinate transformation and its application to wind
turbine analysis", AIAA 2008-1300.
"""
from __future__ import annotations

import re
from typing import Dict, List, Tuple

import numpy as np

_BLADE_PATTERNS = [r'[Bb]lade \d', r'[Bb]lade [Rr]oot \d', r'BD_\d', r'[Bb]\d', r'[Bb]lade\d',
                   r'PitchBearing\d', r'\d']


def _clean(desc: str) -> str:
    return re.sub(r'\s*\(internal DOF index = [^()]*(?:\([^()]*\))?[^()]*\)', '', desc)


def triplet_key(desc: str):
    """Return (key, blade number) where key is the description with the blade digit replaced."""
    d = _clean(desc)
    for pat in _BLADE_PATTERNS:
        m = re.search(pat, d)
        if m:
            tok = m.group()
            b = int(tok[-1])
            key = d[:m.start()] + tok[:-1] + '#' + d[m.end():]
            return key, b
    return None, None


def find_triplets(channels) -> Tuple[List[List[int]], List[int]]:
    """Group rotating-frame channels into blade triplets [i_b1, i_b2, i_b3].

    Returns (triplets, singles) where singles are rotating channels that do not form a
    complete 1-2-3 triplet (e.g. 'BldPitch1' alone); they cannot be transformed."""
    groups: Dict[str, Dict[int, int]] = {}
    order = []
    singles = []
    for ch in channels:
        if not ch.rotating:
            continue
        key, b = triplet_key(ch.desc)
        if key is None:
            singles.append(ch.index)
            continue
        if key not in groups:
            groups[key] = {}
            order.append(key)
        groups[key].setdefault(b, ch.index)
    trip = []
    for key in order:
        g = groups[key]
        if set(g) >= {1, 2, 3}:
            trip.append([g[1], g[2], g[3]])
            singles += [i for bb, i in g.items() if bb not in (1, 2, 3)]
        else:
            singles += list(g.values())
    return trip, sorted(singles)


def second_order_pairs(states) -> Dict[int, int]:
    """Map velocity-state index -> position-state index for 2nd-order states.

    OpenFAST writes each module's 2nd-order states as [q ; q_dot]."""
    by_mod: Dict[str, List[int]] = {}
    for ch in states:
        if ch.deriv_order == 2:
            by_mod.setdefault(ch.module, []).append(ch.index)
    pairs = {}
    for mod, idx in by_mod.items():
        h = len(idx) // 2
        for k in range(h):
            pairs[idx[h + k]] = idx[k]
    return pairs


def T1(psi):
    a = psi + 2 * np.pi * np.arange(3) / 3
    return np.column_stack([np.ones(3), np.cos(a), np.sin(a)])


def T2(psi):
    a = psi + 2 * np.pi * np.arange(3) / 3
    return np.column_stack([np.zeros(3), -np.sin(a), np.cos(a)])


def T3(psi):
    a = psi + 2 * np.pi * np.arange(3) / 3
    return np.column_stack([np.zeros(3), -np.cos(a), -np.sin(a)])


class MBCPlan:
    """Which rows/cols are transformed, worked out once per operating point."""

    def __init__(self, lf):
        self.nx, self.nu, self.ny = len(lf.x), len(lf.u), len(lf.y)
        sx, self.x_single = find_triplets(lf.x)
        pairs = second_order_pairs(lf.x)
        trip_set = {tuple(t) for t in sx}
        self.x_pos2, self.x_vel2, self.x_first = [], [], []
        orphan = []
        for t in sx:
            if all(i in pairs for i in t):                      # a velocity triplet
                p = tuple(pairs[i] for i in t)
                if p in trip_set:
                    self.x_vel2.append((list(t), list(p)))
                else:
                    orphan.append(t)
            elif lf.x[t[0]].deriv_order == 2:
                self.x_pos2.append(list(t))
            else:
                self.x_first.append(list(t))
        # a velocity triplet whose position partner is not a triplet cannot be transformed
        for t in orphan:
            self.x_single += list(t)
        self.x_single.sort()
        self.u_trip, self.u_single = find_triplets(lf.u)
        self.y_trip, self.y_single = find_triplets(lf.y)

    @property
    def has_rotor_states(self):
        return bool(self.x_pos2 or self.x_first)

    def roles(self, table: str):
        """Per channel role after MBC: '' (fixed), 'col', 'cos', 'sin', 'rot' (untransformed)."""
        n = {'x': self.nx, 'u': self.nu, 'y': self.ny}[table]
        r = [''] * n
        trips = {'x': self.x_pos2 + [v for v, _ in self.x_vel2] + self.x_first,
                 'u': self.u_trip, 'y': self.y_trip}[table]
        singles = {'x': self.x_single, 'u': self.u_single, 'y': self.y_single}[table]
        for t in trips:
            r[t[0]], r[t[1]], r[t[2]] = 'col', 'cos', 'sin'
        for i in singles:
            r[i] = 'rot'
        return r

    def transform(self, lf, psi, omega, omega_dot=0.0):
        A, B, C, D = lf.A, lf.B, lf.C, lf.D
        nx = self.nx
        T = np.eye(nx)
        Td = np.zeros((nx, nx))
        t1, t2, t3 = T1(psi), T2(psi), T3(psi)
        for p in self.x_pos2:
            T[np.ix_(p, p)] = t1
            Td[np.ix_(p, p)] = omega * t2
        for v, p in self.x_vel2:
            T[np.ix_(v, v)] = t1
            T[np.ix_(v, p)] = omega * t2
            Td[np.ix_(v, v)] = omega * t2
            Td[np.ix_(v, p)] = omega ** 2 * t3 + omega_dot * t2
        for f in self.x_first:
            T[np.ix_(f, f)] = t1
            Td[np.ix_(f, f)] = omega * t2
        Tu = np.eye(self.nu)
        for t in self.u_trip:
            Tu[np.ix_(t, t)] = t1
        Tyi = np.eye(self.ny)
        t1i = np.linalg.inv(t1)
        for t in self.y_trip:
            Tyi[np.ix_(t, t)] = t1i
        Ti = np.linalg.inv(T)
        An = Ti @ (A @ T - Td)
        Bn = Ti @ B @ Tu
        Cn = Tyi @ C @ T
        Dn = Tyi @ D @ Tu
        return An, Bn, Cn, Dn


def mbc_average(lfs, plan: MBCPlan = None):
    """Transform every azimuth file and average.  Returns dict with A,B,C,D and diagnostics."""
    if plan is None:
        plan = MBCPlan(lfs[0])
    acc = None
    As = []
    for lf in lfs:
        psi = lf.azimuth if np.isfinite(lf.azimuth) else 0.0
        om = lf.rotor_speed if np.isfinite(lf.rotor_speed) else 0.0
        mats = plan.transform(lf, psi, om)
        As.append(mats[0])
        acc = [m.copy() for m in mats] if acc is None else [a + m for a, m in zip(acc, mats)]
    n = len(lfs)
    A, B, C, D = [a / n for a in acc]
    nrm = np.linalg.norm(A)
    var = max(np.linalg.norm(a - A) for a in As) / nrm if nrm > 0 else 0.0
    # raw (no MBC) average for comparison: how periodic were the rotating-frame matrices?
    Araw = sum(lf.A for lf in lfs) / n
    var_raw = max(np.linalg.norm(lf.A - Araw) for lf in lfs) / (np.linalg.norm(Araw) or 1.0)
    return dict(A=A, B=B, C=C, D=D, plan=plan, periodicity_mbc=float(var), A_list=As,
                periodicity_raw=float(var_raw), n_azimuth=n)


def nr_label(desc: str, role: str) -> str:
    """Readable fixed-frame label for a transformed channel."""
    if role in ('col', 'cos', 'sin'):
        key, _ = triplet_key(desc)
        base = key.replace(' blade #', '').replace(' of blade #', '').replace('Blade #', 'Blade').replace('#', '')
        base = re.sub(r'\s+', ' ', base).strip().rstrip(',')
        tag = {'col': 'collective', 'cos': 'cyclic cos', 'sin': 'cyclic sin'}[role]
        return f'{base} [{tag}]'
    return _clean(desc)
