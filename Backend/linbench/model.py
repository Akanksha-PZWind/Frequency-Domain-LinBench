"""One operating point = MBC-averaged LTI model + labelled channels + trim values."""
from __future__ import annotations

import math
import re
from typing import Dict, List, Optional

import numpy as np

from .linfile import read_lin
from .mbc import MBCPlan, mbc_average, triplet_key

# ---------------------------------------------------------------- DOF naming
_ED_DOF = {
    'Sg': ('Platform surge', 'platform'), 'Sw': ('Platform sway', 'platform'),
    'Hv': ('Platform heave', 'platform'), 'R': ('Platform roll', 'platform'),
    'P': ('Platform pitch', 'platform'), 'Y': ('Platform yaw', 'platform'),
    'TFA1': ('Tower FA 1', 'tower'), 'TSS1': ('Tower SS 1', 'tower'),
    'TFA2': ('Tower FA 2', 'tower'), 'TSS2': ('Tower SS 2', 'tower'),
    'Yaw': ('Nacelle yaw', 'nacelle'), 'RFrl': ('Rotor furl', 'nacelle'), 'TFrl': ('Tail furl', 'nacelle'),
    'GeAz': ('Rotor azimuth / speed', 'drivetrain'), 'DrTr': ('Drivetrain torsion', 'drivetrain'),
    'Teet': ('Rotor teeter', 'rotor'),
}
_PTFM = {'Sg': 'Platform surge', 'Sw': 'Platform sway', 'Hv': 'Platform heave',
         'R': 'Platform roll', 'P': 'Platform pitch', 'Y': 'Platform yaw'}
ROLE_TAG = {'col': 'collective', 'cos': 'cyclic', 'sin': 'cyclic', 'rot': 'rotating', '': ''}

FAMILIES = ['platform', 'tower', 'drivetrain', 'blade flap', 'blade edge', 'blade torsion',
            'nacelle', 'rotor', 'hydro/aero states', 'other']


def dof_of_state(ch, role: str):
    """(dof_name, family, component) for a state channel after MBC."""
    d = ch.desc
    m = re.search(r'DOF_(BF|BE)\((\d),(\d)\)', d)
    if m:
        kind, mode = m.group(1), m.group(3)
        fam = 'blade flap' if kind == 'BF' else 'blade edge'
        return (f'Blade {"flap" if kind == "BF" else "edge"} {mode}', fam, role or 'rot')
    m = re.search(r'DOF_(\w+)\)', d)
    if m and m.group(1) in _ED_DOF:
        name, fam = _ED_DOF[m.group(1)]
        return name, fam, ''
    if ch.module.startswith('BD'):
        low = d.lower()
        comp = re.search(r'(translational|rotational)\s+(displacement|velocity|deflection)[^,]*?\b([XYZ])\b', d, re.I)
        if comp:
            axis = comp.group(3).upper()
            if comp.group(1).lower().startswith('trans'):
                kind = {'X': ('flap', 'blade flap'), 'Y': ('edge', 'blade edge'), 'Z': ('axial', 'blade torsion')}[axis]
            else:
                kind = {'X': ('edge-rot', 'blade edge'), 'Y': ('flap-rot', 'blade flap'), 'Z': ('torsion', 'blade torsion')}[axis]
            return f'Blade {kind[0]} (BeamDyn)', kind[1], role or 'rot'
        return 'Blade (BeamDyn)', 'blade flap' if 'flap' in low else 'other', role or 'rot'
    m = re.search(r'Ptfm(Sg|Sw|Hv|R|P|Y)', d)
    if ch.module in ('HD', 'SD', 'ExtPtfm', 'MAP', 'MD') and m:
        return f'{_PTFM[m.group(1)]} (hydro)', 'hydro/aero states', ''
    if ch.module in ('AD', 'HD', 'MAP', 'MD', 'SD', 'IfW', 'SrvD'):
        base = re.sub(r'\d+$', '', ch.name).strip()
        return f'{ch.module} {base}', 'hydro/aero states', role
    return re.sub(r'First time derivative of\s*', '', ch.name), 'other', role


def _strip_deriv(s):
    return re.sub(r'^First time derivative of\s*', '', s)


def fixed_label(ch, role):
    """Human label for a channel in the fixed (MBC) frame."""
    if role in ('col', 'cos', 'sin'):
        key, _ = triplet_key(ch.desc)
        base = re.sub(r'\s*\(internal DOF index = [^()]*(?:\([^()]*\))?[^()]*\)', '', key)
        base = base.replace(' of blade #', '').replace('Blade # ', 'Blade ').replace('blade #', 'blade')
        base = re.sub(r'(\w)#', r'\1', base).replace('#', '')
        base = re.sub(r',\s*\S+$', '', base) if ch.unit and base.endswith(ch.unit) else base
        base = re.sub(r'\s*\([^()]*\)\s*$', '', base).strip().rstrip(',')
        tag = {'col': 'collective', 'cos': 'cos', 'sin': 'sin'}[role]
        return f'{base} [{tag}]'
    base = re.sub(r'\s*\(internal DOF index = [^()]*(?:\([^()]*\))?[^()]*\)', '', ch.desc)
    base = re.sub(r'\s*\([^()]*\)\s*$', '', base)
    if ch.unit and base.rstrip().endswith(', ' + ch.unit):
        base = base.rstrip()[:-len(ch.unit) - 2]
    return base.strip().rstrip(',') + (' [blade 1, rotating]' if role == 'rot' else '')


class OpModel:
    """MBC-averaged state-space model at one operating point."""

    def __init__(self, files: List[str], idx: int = 0, remove_azimuth: bool = True):
        self.idx = idx
        self.files = list(files)
        lfs = [read_lin(f) for f in self.files]
        lfs.sort(key=lambda lf: lf.azimuth % (2 * math.pi) if math.isfinite(lf.azimuth) else 0.0)
        ref = lfs[0]
        for lf in lfs[1:]:
            if len(lf.x) != len(ref.x) or any(a.desc != b.desc for a, b in zip(lf.x, ref.x)):
                raise ValueError(f'{lf.path}: state list differs from {ref.path} (same operating point)')
        self.plan = MBCPlan(ref)
        res = mbc_average(lfs, self.plan)
        self.A, self.B, self.C, self.D = res['A'], res['B'], res['C'], res['D']
        self.periodicity_mbc = res['periodicity_mbc']
        self._A_list = res['A_list']
        self.periodicity_raw = res['periodicity_raw']
        self.n_azimuth = res['n_azimuth']
        self.x, self.u, self.y = ref.x, ref.u, ref.y
        self.xrole, self.urole, self.yrole = self.plan.roles('x'), self.plan.roles('u'), self.plan.roles('y')
        self.wind = float(np.nanmean([lf.wind_speed for lf in lfs]))
        self.omega = float(np.nanmean([lf.rotor_speed for lf in lfs]))
        self.modules = ref.modules
        self.title = ref.title
        self.azimuths = [lf.azimuth for lf in lfs]
        # operating-point values (fixed-frame channels averaged over azimuth)
        self.u_op = np.nanmean([[c.op for c in lf.u] for lf in lfs], axis=0)
        self.y_op = np.nanmean([[c.op for c in lf.y] for lf in lfs], axis=0)
        self.x_op = np.nanmean([[c.op for c in lf.x] for lf in lfs], axis=0)
        # labels
        self.removed_states = []
        self._keep = list(range(self.A.shape[0]))
        if remove_azimuth:
            self._remove_azimuth()
        # periodicity over azimuth of the (reduced) MBC matrices: 0 for an isotropic rotor
        kk = np.ix_(self._keep, self._keep)
        nrm = np.linalg.norm(self.A) or 1.0
        self.periodicity_mbc = float(max(np.linalg.norm(a[kk] - self.A) for a in self._A_list) / nrm)
        Araw = sum(lf.A[kk] for lf in lfs) / len(lfs)
        self.periodicity_raw = float(max(np.linalg.norm(lf.A[kk] - Araw) for lf in lfs) / (np.linalg.norm(Araw) or 1.0))
        self.az_modes = self._azimuth_sensitivity([a[kk] for a in self._A_list])
        del self._A_list
        self.x_dof = [dof_of_state(ch, r) for ch, r in zip(self.x, self.xrole)]
        self.x_label = [fixed_label(ch, r) for ch, r in zip(self.x, self.xrole)]
        self.u_label = [fixed_label(ch, r) for ch, r in zip(self.u, self.urole)]
        self.y_label = [fixed_label(ch, r) for ch, r in zip(self.y, self.yrole)]
        self._eig = None
        self.file_diag = self._file_diag(lfs)

    def _azimuth_sensitivity(self, A_list, fmax_hz=3.0):
        """How far each mode of the averaged model moves in the model of each single azimuth
        (MBC applied, no averaging).  Large values mean the azimuth files disagree: rotor anisotropy,
        or files linearised about different operating points (e.g. rotor speed not converged)."""
        lam = np.linalg.eigvals(self.A)
        eigs = [np.linalg.eigvals(a) for a in A_list]
        rows = []
        for l in lam:
            wn = abs(l)
            if l.imag <= 1e-9 or wn / (2 * math.pi) > fmax_hz:
                continue
            f0, z0 = wn / (2 * math.pi), -l.real / wn
            df, dz = [], []
            for e in eigs:
                m = e[np.argmin(np.abs(e - l))]
                df.append((abs(m) / (2 * math.pi) - f0) / f0)
                dz.append(-m.real / abs(m) - z0)
            rows.append(dict(re=float(l.real), im=float(l.imag), fn=float(f0), zeta=float(z0), df=df, dz=dz))
        return rows

    def _file_diag(self, lfs):
        """Operating values of every azimuth file (sorted by azimuth) for the debugging table."""
        si, so = self.std_inputs(), self.std_outputs()
        pitch_cols = [si['pitch']] if si['pitch'] is not None else []
        if si['pitch'] is not None and self.urole[si['pitch']] == 'col':
            pitch_cols = [t for t in self.plan.u_trip if t[0] == si['pitch']][0]
        out = []
        for k, lf in enumerate(lfs):
            uo = [c.op for c in lf.u]
            yo = [c.op for c in lf.y]
            out.append(dict(
                file=lf.path.replace('\\', '/').split('/')[-1],
                azimuth_deg=math.degrees(lf.azimuth) % 360 if math.isfinite(lf.azimuth) else None,
                time=lf.header.get('time'), rotor_rpm=lf.rotor_speed * 30 / math.pi,
                wind=lf.wind_speed,
                pitch_deg=math.degrees(float(np.mean([uo[i] for i in pitch_cols]))) if pitch_cols else None,
                gen_torque=uo[si['torque']] if si['torque'] is not None else None,
                gen_rpm=yo[so['genspeed']] if so['genspeed'] is not None else None,
                max_df=max((abs(r['df'][k]) for r in self.az_modes), default=None),
                max_dz=max((abs(r['dz'][k]) for r in self.az_modes), default=None)))
        return out

    def _remove_azimuth(self):
        """Drop the generator-azimuth POSITION state (ROSCO does the same, trim_system(rm_azimuth=True)).

        After azimuth averaging nothing physical depends on absolute rotor azimuth, but the
        averaged A keeps small spurious d/d(azimuth) terms that pair the azimuth integrator with
        the rotor-speed state into a fictitious real pole pair (one of them unstable)."""
        az = [i for i, ch in enumerate(self.x) if 'DOF_GeAz' in ch.desc and ch.deriv_order == 2
              and not ch.name.lower().startswith('first time derivative')]
        if not az:
            return
        keep = [i for i in range(len(self.x)) if i not in az]
        remap = {old: new for new, old in enumerate(keep)}
        self._keep = keep
        self.A = self.A[np.ix_(keep, keep)]
        self.B = self.B[keep, :]
        self.C = self.C[:, keep]
        self.x_op = self.x_op[keep]
        self.removed_states = [self.x[i].desc for i in az]
        self.x = [self.x[i] for i in keep]
        self.xrole = [self.xrole[i] for i in keep]
        pl = self.plan
        pl.x_pos2 = [[remap[i] for i in t] for t in pl.x_pos2]
        pl.x_first = [[remap[i] for i in t] for t in pl.x_first]
        pl.x_vel2 = [([remap[i] for i in v], [remap[i] for i in p]) for v, p in pl.x_vel2]
        pl.x_single = [remap[i] for i in pl.x_single if i in remap]
        pl.nx = len(keep)

    # ----------------------------------------------------------- channels
    def find_input(self, *patterns) -> Optional[int]:
        for pat in patterns:
            for i, ch in enumerate(self.u):
                if re.search(pat, ch.desc, re.I) and self.urole[i] in ('', 'col'):
                    return i
        return None

    def find_output(self, *patterns) -> Optional[int]:
        for pat in patterns:
            for i, ch in enumerate(self.y):
                if re.search(pat, ch.desc, re.I) and self.yrole[i] in ('', 'col'):
                    return i
        return None

    def std_inputs(self) -> Dict[str, Optional[int]]:
        pitch = self.find_input(r'collective blade-pitch command')
        if pitch is None:
            pitch = self.find_input(r'Blade 1 pitch command')   # role 'col' after MBC = collective
        return dict(pitch=pitch, torque=self.find_input(r'Generator torque'),
                    wind=self.find_input(r'horizontal wind speed'), wave=self.find_input(r'wave elevation'))

    def std_outputs(self) -> Dict[str, Optional[int]]:
        return dict(genspeed=self.find_output(r'\bGenSpeed\b'), rotspeed=self.find_output(r'\bRotSpeed\b'),
                    genpwr=self.find_output(r'\bGenPwr\b'), gentq=self.find_output(r'\bGenTq\b'),
                    tsr=self.find_output(r'\bRtTSR\b'), cp=self.find_output(r'\bRtAeroCp\b'),
                    ct=self.find_output(r'\bRtAeroCt\b'), thrust=self.find_output(r'\bRotThrust\b', r'RtAeroFxh'),
                    ptfmpitch=self.find_output(r'\bPtfmPitch\b'),
                    nacacc_fa=self.find_output(r'\bNcIMUTAxs\b', r'\bYawBrTAxp\b'),
                    nacracc=self.find_output(r'\bNcIMURAys\b'),
                    ttdspfa=self.find_output(r'\bTTDspFA\b', r'YawBrTDxp'))

    def trim(self) -> Dict:
        si, so = self.std_inputs(), self.std_outputs()
        pitch = float(self.u_op[si['pitch']]) if si['pitch'] is not None else float('nan')
        if si['pitch'] is not None and self.urole[si['pitch']] == 'col':
            trip = [t for t in self.plan.u_trip if t[0] == si['pitch']][0]
            pitch = float(np.mean(self.u_op[trip]))
        tq = float(self.u_op[si['torque']]) if si['torque'] is not None else float('nan')

        def yo(k):
            return float(self.y_op[so[k]]) if so[k] is not None else float('nan')
        gs = yo('genspeed')
        return dict(wind=self.wind, rotor_rpm=self.omega * 30 / math.pi, rotor_rad_s=self.omega,
                    pitch_deg=math.degrees(pitch) if math.isfinite(pitch) else float('nan'), pitch_rad=pitch,
                    gen_torque=tq, gen_rpm=gs, power_kw=yo('genpwr'), tsr=yo('tsr'), cp=yo('cp'),
                    ct=yo('ct'), thrust_kn=yo('thrust'), ptfm_pitch_deg=yo('ptfmpitch'))
