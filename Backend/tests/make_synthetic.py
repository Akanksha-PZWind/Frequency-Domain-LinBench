"""Write a synthetic 3-bladed rotor as OpenFAST-format .lin files.

A lumped model derived with Lagrange's equations in the rotating frame:
  DOFs  x (tower FA), y (tower SS), phi (generator azimuth), q_f,b (flap), q_e,b (edge)
  M(psi) q'' + G(psi) q' + K(psi) q = F u
Isotropic rotor => the MBC-transformed system is exactly time-invariant, and the
uncoupled cyclic flap pair must appear at f_flap +/- Omega (forward / backward whirl).
The files are written with shuffled names so that name order != azimuth / wind order.
"""
import os
import random
import sys

import numpy as np

HDR = """
Linearized model: Predictions were generated on 01-Jan-2026 at 00:00:00 using OpenFAST, compiled as a 64-bit application using double precision at commit v3.5.0
 linked with  NWTC Subroutine Library; ElastoDyn; InflowWind; ServoDyn

Description from the FAST input file: Synthetic isotropic rotor (LinBench validation case)

Simulation information:
   Simulation time:                  {t:10.4f} s
   Rotor Speed:                      {om:10.4f} rad/s
   Azimuth:                          {az:10.4f} rad
   Wind Speed:                       {U:10.4f} m/s
   Number of continuous states:      {nx:6d}
   Number of discrete states:           0
   Number of constraint states:         0
   Number of inputs:                 {nu:6d}
   Number of outputs:                {ny:6d}
   Jacobians included in this file?    No

"""

P = dict(m=5000.0, r=20.0, Mx=3.5e5, My=3.5e5, J=4.0e7, f_fa=0.32, f_ss=0.31, f_flap=0.70, f_edge=1.10,
         z_fa=0.02, z_ss=0.01, z_flap=0.25, z_edge=0.01, R=63.0)


def matrices(psi, Om, U, pitch):
    m, r = P['m'], P['r']
    c = np.cos(psi + 2 * np.pi * np.arange(3) / 3)
    s = np.sin(psi + 2 * np.pi * np.arange(3) / 3)
    n = 9  # x, y, phi, f1..3, e1..3
    X, Y, PH, F, E = 0, 1, 2, [3, 4, 5], [6, 7, 8]
    M = np.zeros((n, n)); G = np.zeros((n, n)); K = np.zeros((n, n))
    M[X, X] = P['Mx'] + 3 * m; M[Y, Y] = P['My'] + 3 * m; M[PH, PH] = P['J'] + 3 * m * r * r
    for b in range(3):
        M[F[b], F[b]] = m; M[E[b], E[b]] = m
        M[X, F[b]] = M[F[b], X] = m
        M[Y, E[b]] = M[E[b], Y] = m * c[b]
        M[PH, E[b]] = M[E[b], PH] = m * r
        G[Y, E[b]] = -2 * Om * m * s[b]
        K[Y, E[b]] = -Om ** 2 * m * c[b]
    w = lambda f: 2 * np.pi * f
    load = 0.5 + 0.5 * min(U / 12.0, 1.0)               # aero damping grows with wind
    K[X, X] = w(P['f_fa']) ** 2 * (P['Mx'] + 3 * m)
    K[Y, Y] = w(P['f_ss']) ** 2 * (P['My'] + 3 * m)
    G[X, X] = 2 * P['z_fa'] * w(P['f_fa']) * (P['Mx'] + 3 * m)
    G[Y, Y] = 2 * P['z_ss'] * w(P['f_ss']) * (P['My'] + 3 * m)
    G[PH, PH] = 2.0e6 * (1 + U / 20)                       # generator + aero torque slope
    for b in range(3):
        K[F[b], F[b]] = m * w(P['f_flap']) ** 2
        K[E[b], E[b]] = m * (w(P['f_edge']) ** 2 + Om ** 2)   # rotating edge freq = f_edge
        G[F[b], F[b]] = 2 * P['z_flap'] * load * w(P['f_flap']) * m
        G[E[b], E[b]] = 2 * P['z_edge'] * w(P['f_edge']) * m
    # inputs: wind, pitch b1..b3, yaw (unused), gen torque, collective pitch
    nu = 6
    Fm = np.zeros((n, nu))
    for b in range(3):
        Fm[F[b], 0] = 900.0 * load; Fm[E[b], 0] = 150.0
        Fm[F[b], 1 + b] = -9.0e4 * load; Fm[E[b], 1 + b] = -1.2e4
        Fm[F[b], 5] = -9.0e4 * load; Fm[E[b], 5] = -1.2e4
    Fm[PH, 0] = 1.5e5 * load
    Fm[PH, [1, 2, 3, 5]] = [-1.0e6 * load] * 3 + [-3.0e6 * load]
    Fm[PH, 4] = -1.0
    Mi = np.linalg.inv(M)
    A = np.block([[np.zeros((n, n)), np.eye(n)], [-Mi @ K, -Mi @ G]])
    B = np.vstack([np.zeros((n, nu)), Mi @ Fm])
    # outputs
    rows_C, rows_D, outs = [], [], []

    def out(name, unit, rot, crow, drow, op):
        rows_C.append(crow); rows_D.append(drow); outs.append((name, unit, rot, op))
    z = np.zeros(2 * n); zu = np.zeros(nu)
    e = lambda k, sc=1.0: (lambda v: (v.__setitem__(k, sc), v)[1])(np.zeros(2 * n))
    out('ED GenSpeed', 'rpm', 'F', e(n + PH, 30 / np.pi), zu, Om * 30 / np.pi)
    out('ED TTDspFA', 'm', 'F', e(X), zu, 0.1)
    out('ED TTDspSS', 'm', 'F', e(Y), zu, 0.0)
    acc = A[n + X, :].copy(); acc_d = B[n + X, :].copy()
    out('ED YawBrTAxp', 'm/s^2', 'F', acc, acc_d, 0.0)
    for b in range(3):
        out(f'ED RootMyc{b + 1}', 'kN-m', 'T', e(F[b], m * w(P['f_flap']) ** 2 / 1000), zu, 8000.0)
    for b in range(3):
        out(f'ED RootMxc{b + 1}', 'kN-m', 'T', e(E[b], m * w(P['f_edge']) ** 2 / 1000), zu, 2000.0)
    bp = np.zeros(nu); bp[1] = 180 / np.pi
    out('ED BldPitch1', 'deg', 'T', z, bp, np.degrees(pitch))
    return A, B, np.array(rows_C), np.array(rows_D), outs


STATES = ['ED 1st tower fore-aft bending mode DOF (internal DOF index = DOF_TFA1), m',
          'ED 1st tower side-to-side bending mode DOF (internal DOF index = DOF_TSS1), m',
          'ED Variable speed generator DOF (internal DOF index = DOF_GeAz), rad'] + \
         [f'ED 1st flapwise bending-mode DOF of blade {b} (internal DOF index = DOF_BF({b},1)), m' for b in (1, 2, 3)] + \
         [f'ED 1st edgewise bending-mode DOF of blade {b} (internal DOF index = DOF_BE({b},1)), m' for b in (1, 2, 3)]
ROT = [False, False, False] + [True] * 6
INPUTS = [('IfW Extended input: horizontal wind speed (steady/uniform wind), m/s', 'F'),
          ('ED Blade 1 pitch command, rad', 'T'), ('ED Blade 2 pitch command, rad', 'T'),
          ('ED Blade 3 pitch command, rad', 'T'), ('ED Generator torque, Nm', 'F'),
          ('ED Extended input: collective blade-pitch command, rad', 'F')]


def _vel_desc(d):
    head, tail = d.split(' ', 1)
    tail = tail.replace(', m', ', m/s').replace(', rad', ', rad/s')
    return f'{head} First time derivative of {tail}'


def _table(rows):
    s = '   Row/Column Operating Point                     Rotating Frame? Derivative Order Description\n'
    s += '   ---------- ---------------                     --------------- ---------------- -----------\n'
    for i, (op, rot, order, desc) in enumerate(rows, 1):
        s += f'{i:>11d}    {op:10.3E}                                 {rot}               {order}         {desc}\n'
    return s + '\n'


def _mat(name, M):
    s = f'{name}: {M.shape[0]} x {M.shape[1]}\n'
    for row in M:
        s += ' ' + ' '.join(f'{v:14.7E}' for v in row) + '\n'
    return s + '\n'


def lin_text(U, psi, Om, t):
    """One .lin file of the synthetic rotor at wind speed U, azimuth psi, rotor speed Om, time t."""
    pitch = max(0.0, np.radians(1.2 * (U - 11.4)))
    A, B, C, D, outs = matrices(psi, Om, U, pitch)
    xs = [(0.0, 'T' if ROT[i] else 'F', 2, STATES[i]) for i in range(9)]
    xs[2] = (psi, 'F', 2, STATES[2])
    xs += [(Om if i == 2 else 0.0, 'T' if ROT[i] else 'F', 2, _vel_desc(STATES[i])) for i in range(9)]
    dxs = [((xs[i + 9][0] if i < 9 else 0.0), r, o, 'First time derivative of ' + d)
           for i, (_, r, o, d) in enumerate(xs)]   # d/dt(position) = velocity op
    us = [(U if i == 0 else (pitch if i in (1, 2, 3, 5) else (4.0e4 if i == 4 else 0.0)), rot, 0, d)
          for i, (d, rot) in enumerate(INPUTS)]
    ys = [(op, rot, 0, f'{name}, ({unit})') for name, unit, rot, op in outs]
    txt = HDR.format(t=t, om=Om, az=psi, U=U, nx=18, nu=6, ny=len(ys))
    txt += 'Order of continuous states:\n' + _table(xs)
    txt += 'Order of continuous state derivatives:\n' + _table(dxs)
    txt += 'Order of inputs:\n' + _table(us)
    txt += 'Order of outputs:\n' + _table(ys)
    txt += '\nLinearized state matrices:\n\n' + _mat('A', A) + _mat('B', B) + _mat('C', C) + _mat('D', D)
    return txt


def rated_speed(U):
    return min(1.267, 7.5 * U / P['R'])


def write_files(folder, cases, prefix='case'):
    """cases: list of (U, psi, Om, t).  Files are numbered in list order."""
    os.makedirs(folder, exist_ok=True)
    for k, (U, psi, Om, t) in enumerate(cases, 1):
        with open(os.path.join(folder, f'{prefix}.{k}.lin'), 'w') as fh:
            fh.write(lin_text(U, psi, Om, t))
    return folder


def write_case(folder, winds=(6, 8, 10, 12, 14, 17, 20, 24), n_az=8, seed=3):
    os.makedirs(folder, exist_ok=True)
    rng = random.Random(seed)
    names = list(range(len(winds) * n_az))
    rng.shuffle(names)
    k = 0
    for U in winds:
        Om = rated_speed(U)
        for j in range(n_az):
            psi = 2 * np.pi * j / n_az + 0.013
            with open(os.path.join(folder, f'synth_{names[k]:03d}.lin'), 'w') as fh:
                fh.write(lin_text(U, psi, Om, 100 + j))
            k += 1
    return folder


if __name__ == '__main__':
    print(write_case(sys.argv[1] if len(sys.argv) > 1 else 'synthetic_rotor'))
