"""Mode-shape animation of the turbine for the "Campbell visualization diagram".

Built from animate_mode_shape.py (tower + nacelle + 3 blades wireframe, cantilever-beam spatial shapes,
MBC reconstruction of the blade DOFs, rigid carry-through of the tower-top motion). Differences:

* No pyFAST and no matplotlib: the mode comes from LinBench's own eigen-analysis (modal.analyse_modes)
  of the MBC-averaged model, so the animated mode is exactly the point clicked in the Campbell diagram
  (operating point `op`, eigenvalue index `k`).
* The DOF amplitudes are COMPLEX (magnitude and phase from the eigenvector). Each frame uses
  Re(a · e^{iφ}), so phase differences are kept (e.g. cos/sin blade components 90° apart show the
  forward / backward whirl travelling round the rotor).
* Platform DOFs (floating turbines), nacelle yaw and drivetrain torsion are animated as rigid motions.
* The frames are computed here and sent as point lists; the WPF app only projects and draws them.

Coordinates: X downwind, Y lateral, Z up; tower base at the origin; blade 1 points up at azimuth 0.
"""
from __future__ import annotations

import glob
import math
import os
import re

import numpy as np

# ElastoDyn names of the DOFs we animate (LinBench DOF name, MBC component) -> key
TRANSLATION_KEYS = ('TowerFA1', 'TowerFA2', 'TowerSS1', 'TowerSS2',
                    'BldFlap1_Coll', 'BldFlap1_Cos', 'BldFlap1_Sin', 'BldFlap2_Coll', 'BldFlap2_Cos', 'BldFlap2_Sin',
                    'BldEdge1_Coll', 'BldEdge1_Cos', 'BldEdge1_Sin', 'BldEdge2_Coll', 'BldEdge2_Cos', 'BldEdge2_Sin',
                    'PtfmSurge', 'PtfmSway', 'PtfmHeave')
ROTATION_KEYS = ('DrivetrainTorsion', 'NacelleYaw', 'PtfmRoll', 'PtfmPitch', 'PtfmYaw')
KEYS = TRANSLATION_KEYS + ROTATION_KEYS
NICE = {'TowerFA1': 'Tower fore-aft 1', 'TowerFA2': 'Tower fore-aft 2', 'TowerSS1': 'Tower side-side 1',
        'TowerSS2': 'Tower side-side 2', 'DrivetrainTorsion': 'Drivetrain torsion', 'NacelleYaw': 'Nacelle yaw',
        'PtfmSurge': 'Platform surge', 'PtfmSway': 'Platform sway', 'PtfmHeave': 'Platform heave',
        'PtfmRoll': 'Platform roll', 'PtfmPitch': 'Platform pitch', 'PtfmYaw': 'Platform yaw'}
_COMP = {'col': 'Coll', 'cos': 'Cos', 'sin': 'Sin'}
_PLAT = {'Platform surge': 'PtfmSurge', 'Platform sway': 'PtfmSway', 'Platform heave': 'PtfmHeave',
         'Platform roll': 'PtfmRoll', 'Platform pitch': 'PtfmPitch', 'Platform yaw': 'PtfmYaw'}

DEFAULT_LENGTHS = dict(tower_len=87.6, blade_len=61.5, hub_radius=1.5)   # NREL 5 MW


def nice_name(key):
    if key in NICE:
        return NICE[key]
    m = re.match(r'Bld(Flap|Edge)(\d)_(Coll|Cos|Sin)', key)
    if m:
        return f"Blade {m.group(1).lower()} {m.group(2)} · {dict(Coll='collective', Cos='cosine', Sin='sine')[m.group(3)]}"
    return key


# ---------------------------------------------------------------- spatial shapes (from animate_mode_shape.py)
def cantilever_mode_shape(x_over_L, mode_num=1):
    """Normalised (tip = 1) Euler-Bernoulli cantilever bending shape, mode_num 1 or 2."""
    betaL = {1: 1.87510407, 2: 4.69409113}.get(mode_num, 1.87510407)
    sig = (np.sinh(betaL) - np.sin(betaL)) / (np.cosh(betaL) + np.cos(betaL))
    x = np.asarray(x_over_L, float)
    shape = (np.cosh(betaL * x) - np.cos(betaL * x)) - sig * (np.sinh(betaL * x) - np.sin(betaL * x))
    tip = shape[-1] if np.ndim(shape) > 0 else shape
    return shape / tip if abs(tip) > 1e-12 else shape


# ---------------------------------------------------------------- DOF amplitudes of one mode
def _key_of(name, comp):
    m = re.match(r'Tower (FA|SS) (\d)', name)
    if m:
        return f'Tower{m.group(1)}{m.group(2)}'
    m = re.match(r'Blade (flap|edge) (\d)$', name)
    if m and comp in _COMP:
        return f'Bld{m.group(1).capitalize()}{m.group(2)}_{_COMP[comp]}'
    if name == 'Drivetrain torsion':
        return 'DrivetrainTorsion'
    if name == 'Nacelle yaw':
        return 'NacelleYaw'
    return _PLAT.get(name)


def mode_dofs(om, k, lengths):
    """Complex, normalised DOF amplitudes of eigenvector k of the averaged model `om`.

    Every amplitude is made dimensionless: translations divided by the length they act on (tower DOFs and
    platform translations by the tower height, blade DOFs by the blade length), rotations in rad. The
    largest one is scaled to 1 with phase 0, so `scale` in the animation is the largest deflection as a
    fraction of that length (or in rad)."""
    lam, V, _ = om._eig_raw
    v = V[:, k]
    amp = {}
    for i, (ch, (name, fam, comp)) in enumerate(zip(om.x, om.x_dof)):
        if ch.name.startswith('First time derivative') or ch.desc.startswith('First time derivative'):
            continue                                   # velocity states carry the same shape
        key = _key_of(name, comp)
        if key is None:
            continue
        a = complex(v[i])
        if key.startswith('Bld'):
            a /= lengths['blade_len']
        elif key in TRANSLATION_KEYS:
            a /= lengths['tower_len']
        if abs(a) > abs(amp.get(key, 0)):
            amp[key] = a
    ref = max(amp.values(), key=abs) if amp else 0
    if abs(ref) > 0:
        amp = {k_: a / ref for k_, a in amp.items()}  # largest DOF: magnitude 1, phase 0
    full = {key: amp.get(key, 0j) for key in KEYS}
    return full, complex(lam[k])


# ---------------------------------------------------------------- geometry (from animate_mode_shape.py, extended)
class TurbineGeometry:
    def __init__(self, tower_len, blade_len, hub_radius, n_tower=25, n_blade=25):
        self.H, self.L, self.Rh = float(tower_len), float(blade_len), float(hub_radius)
        self.z_tower = np.linspace(0.0, self.H, n_tower)
        self.r_blade = np.linspace(0.0, self.L, n_blade)
        self.psi = [2 * math.pi * b / 3 for b in range(3)]          # blade b azimuth, blade 1 up
        R = self.Rh + self.L
        self.x_hub = -0.06 * R                                      # rotor slightly upwind of the tower
        self.x_rear = 0.10 * R

    def _rotor_blade(self, psi, flap, edge):
        r = self.Rh + self.r_blade
        tang = np.array([0.0, math.cos(psi), -math.sin(psi)])        # direction of increasing azimuth
        pts = np.vstack([np.full_like(r, self.x_hub), r * math.sin(psi), self.H + r * math.cos(psi)])
        pts[0] += flap                                               # out of plane: downwind
        pts += np.outer(tang, edge)                                  # in plane: tangential
        return pts

    def undeformed(self):
        z = np.zeros_like(self.z_tower)
        return dict(tower=np.vstack([z, z, self.z_tower]),
                    nacelle=np.array([[self.x_rear, self.x_hub], [0.0, 0.0], [self.H, self.H]]),
                    blades=[self._rotor_blade(p, 0.0, 0.0) for p in self.psi])

    def deformed(self, amp, scale, phase):
        e = complex(math.cos(phase), math.sin(phase))
        c = {k: (a * e).real for k, a in amp.items()}               # Re(a e^{i phase})
        s1t, s2t = cantilever_mode_shape(self.z_tower / self.H, 1), cantilever_mode_shape(self.z_tower / self.H, 2)
        dx = scale * self.H * (c['TowerFA1'] * s1t + c['TowerFA2'] * s2t)
        dy = scale * self.H * (c['TowerSS1'] * s1t + c['TowerSS2'] * s2t)
        tower = np.vstack([dx, dy, self.z_tower])
        top = np.array([dx[-1], dy[-1], 0.0])

        f1, f2 = cantilever_mode_shape(self.r_blade / self.L, 1), cantilever_mode_shape(self.r_blade / self.L, 2)
        twist = scale * c['DrivetrainTorsion']
        blades = []
        for psi in self.psi:
            def mbc(base):     # blade value = collective + cosine cos(psi) + sine sin(psi)
                return c[base + '_Coll'] + c[base + '_Cos'] * math.cos(psi) + c[base + '_Sin'] * math.sin(psi)
            flap = scale * self.L * (mbc('BldFlap1') * f1 + mbc('BldFlap2') * f2)
            edge = scale * self.L * (mbc('BldEdge1') * f1 + mbc('BldEdge2') * f2)
            blades.append(self._rotor_blade(psi + twist, flap, edge))
        nac = np.array([[self.x_rear, self.x_hub], [0.0, 0.0], [self.H, self.H]])

        # nacelle yaw about the tower axis, then the tower-top deflection carries the rotor along
        yaw = scale * c['NacelleYaw']
        rz = _rot_z(yaw)
        axis = np.array([[0.0], [0.0], [self.H]])
        nac = rz @ (nac - axis) + axis + top[:, None]
        blades = [rz @ (b - axis) + axis + top[:, None] for b in blades]

        # platform: rigid motion of the whole turbine about the tower base
        rot = _rot_z(scale * c['PtfmYaw']) @ _rot_y(scale * c['PtfmPitch']) @ _rot_x(scale * c['PtfmRoll'])
        shift = scale * self.H * np.array([[c['PtfmSurge']], [c['PtfmSway']], [c['PtfmHeave']]])
        if np.any(rot != np.eye(3)) or np.any(shift):
            tower = rot @ tower + shift
            nac = rot @ nac + shift
            blades = [rot @ b + shift for b in blades]
        return dict(tower=tower, nacelle=nac, blades=blades)


def _rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def _rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def _rot_z(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


# ---------------------------------------------------------------- turbine size from ElastoDyn, if it is there
def _ed_value(text, key):
    m = re.search(r'^\s*([-+0-9.eEdD]+)\s+' + key + r'\b', text, re.M)
    if not m:
        return None
    try:
        return float(m.group(1).replace('D', 'E').replace('d', 'e'))
    except ValueError:
        return None


def turbine_lengths(lin_dir):
    """TowerHt / TipRad / HubRad from an ElastoDyn input file next to the .lin files (or one folder up).
    Falls back to the NREL 5 MW values."""
    out = dict(DEFAULT_LENGTHS, source='NREL 5 MW defaults (no ElastoDyn file found next to the .lin files)')
    if not lin_dir:
        return out
    roots = [lin_dir, os.path.dirname(os.path.abspath(lin_dir))]
    cands = []
    for r in roots:
        cands += sorted(glob.glob(os.path.join(r, '*.dat')))
    for f in sorted(cands, key=lambda p: ('elastodyn' not in os.path.basename(p).lower(), p))[:40]:
        try:
            if os.path.getsize(f) > 2_000_000:
                continue
            with open(f, errors='replace') as fh:
                text = fh.read()
        except OSError:
            continue
        tower, tip, hub = _ed_value(text, 'TowerHt'), _ed_value(text, 'TipRad'), _ed_value(text, 'HubRad')
        if tower and tip:
            hub = hub if hub is not None else DEFAULT_LENGTHS['hub_radius']
            return dict(tower_len=tower, blade_len=max(tip - hub, 1.0), hub_radius=hub,
                        source=f'ElastoDyn: {os.path.basename(f)}')
    return out


# ---------------------------------------------------------------- the request
def animation(om, k, label, lengths, scale=0.15, n_frames=48):
    """Frames of one oscillation cycle of mode k at operating point om."""
    amp, lam = mode_dofs(om, k, lengths)
    geom = TurbineGeometry(lengths['tower_len'], lengths['blade_len'], lengths['hub_radius'])
    n_frames = int(min(max(n_frames, 12), 120))

    def pack(g):
        return dict(tower=np.round(g['tower'], 3), nacelle=np.round(g['nacelle'], 3),
                    blades=[np.round(b, 3) for b in g['blades']])
    frames = [pack(geom.deformed(amp, scale, 2 * math.pi * i / n_frames)) for i in range(n_frames)]
    wn = abs(lam)
    dofs = sorted(([nice_name(key), abs(a), math.degrees(math.atan2(a.imag, a.real))]
                   for key, a in amp.items() if abs(a) > 0.02), key=lambda r: -r[1])
    return dict(k=int(k), label=label, fn=wn / (2 * math.pi), fd=abs(lam.imag) / (2 * math.pi),
                zeta=(-lam.real / wn) if wn > 0 else None, scale=scale, n_frames=n_frames,
                tower_len=lengths['tower_len'], blade_len=lengths['blade_len'], hub_radius=lengths['hub_radius'],
                length_source=lengths.get('source', ''), dofs=dofs,
                ref=pack(geom.undeformed()), frames=frames)
