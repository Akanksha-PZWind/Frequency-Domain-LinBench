"""Find .lin files in a folder and group them into operating points by CONTENT.

File names are never used for ordering.  Each file's header gives wind speed, rotor
speed, azimuth and simulation time.  Files are clustered on wind speed and ordered by
azimuth.  A wind speed is split into several operating points only when its azimuths are
duplicated (two complete sets): first by file-name root, then by rotor speed.  Rotor
speed that merely differs between the azimuths of ONE set does not split it (that would
leave fragments averaged over a few azimuths); it is diagnosed instead:
  drift     Omega follows simulation time   -> rotor still accelerating, trim not converged
  periodic  Omega follows azimuth (1P/3P)    -> tower shadow / shear ripple, expected
  scatter   neither                          -> files from different runs or settings
"""
from __future__ import annotations

import math
import os
import re
from dataclasses import dataclass, field
from typing import List

import numpy as np

from . import cache
from .linfile import read_many

_ROOT_RE = re.compile(r'^(.*?)(?:\.(\d+))?\.lin$', re.IGNORECASE)


@dataclass
class OpGroup:
    wind_speed: float
    rotor_speed: float
    files: List[str] = field(default_factory=list)
    azimuths: List[float] = field(default_factory=list)
    roots: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    speeds: List[float] = field(default_factory=list)       # rotor speed of each file [rad/s]
    times: List[float] = field(default_factory=list)        # simulation time of each file [s]
    omega_check: dict = field(default_factory=dict)

    def to_dict(self):
        az = [math.degrees(a) % 360 for a in self.azimuths]
        return dict(wind_speed=self.wind_speed, rotor_speed=self.rotor_speed,
                    rotor_rpm=self.rotor_speed * 30 / math.pi, n_files=len(self.files),
                    files=[os.path.basename(f) for f in self.files], azimuth_deg=az,
                    file_rpm=[w * 30 / math.pi for w in self.speeds], file_time=self.times,
                    omega_check=self.omega_check, roots=sorted(set(self.roots)), warnings=self.warnings)


def _r2(y, X):
    """Adjusted R^2 of a least-squares fit y ~ X (X includes the constant column), so a
    5-term azimuth Fourier fit does not beat a 1-term time trend just by having more terms."""
    coef, *_ = np.linalg.lstsq(X, y, rcond=None)
    ss = float(np.sum((y - y.mean()) ** 2))
    if ss <= 0:
        return 0.0, coef
    r2 = 1.0 - float(np.sum((y - X @ coef) ** 2)) / ss
    n, p = X.shape[0], X.shape[1] - 1
    return (1.0 - (1.0 - r2) * (n - 1) / (n - p - 1) if n - p - 1 > 0 else r2), coef


def rotor_speed_check(speeds, azimuths, times):
    """Classify how rotor speed varies over the azimuth files of one operating point."""
    w = np.asarray(speeds, float)
    ok = np.isfinite(w)
    out = dict(spread=0.0, kind='none', level='ok', slope_rpm_s=None, r2_time=None, r2_azimuth=None, message='')
    if ok.sum() < 2 or np.mean(w[ok]) <= 0:
        return out
    w, psi, t = w[ok], np.asarray(azimuths, float)[ok], np.asarray(times, float)[ok]
    mean = float(w.mean())
    spread = float(np.ptp(w) / mean)
    out['spread'] = spread
    if spread < 1e-5:
        return out
    one = np.ones_like(w)
    r2_t = None
    if np.all(np.isfinite(t)) and np.ptp(t) > 0:
        r2_t, c = _r2(w, np.column_stack([one, t]))
        out['slope_rpm_s'] = float(c[1] * 30 / math.pi)
    r2_a = None
    if np.all(np.isfinite(psi)) and len(w) >= 4:
        cols = [one, np.cos(psi), np.sin(psi)]
        if len(w) >= 7:
            cols += [np.cos(3 * psi), np.sin(3 * psi)]
        r2_a, _ = _r2(w, np.column_stack(cols))
    out.update(r2_time=r2_t, r2_azimuth=r2_a)
    pct = f'{spread * 100:.2g} %'
    if r2_t is not None and r2_t >= 0.8 and (r2_a is None or r2_t >= r2_a):
        out['kind'] = 'drift'
        out['message'] = (f'rotor speed drifts by {pct} over the azimuth sweep ({out["slope_rpm_s"]:+.3g} rpm/s): the rotor '
                          'was still accelerating, so the trim had not converged. In OpenFAST use CalcSteady = True with a '
                          'tighter TrimTol, or linearise later (larger LinTimes / TMax).')
    elif r2_a is not None and r2_a >= 0.8:
        out['kind'] = 'periodic'
        out['message'] = (f'rotor speed varies by {pct} with azimuth (periodic, e.g. tower shadow or shear): '
                          'expected from a converged periodic trim; MBC uses each file\'s own speed.')
    else:
        out['kind'] = 'scatter'
        out['message'] = (f'rotor speed differs by {pct} between azimuths with no trend in time or azimuth: '
                          'the files may come from different simulations or settings.')
    # periodic ripple is physics; drift and scatter mean the azimuths are not one steady operating point
    lim_warn, lim_note = (0.02, 0.005) if out['kind'] == 'periodic' else (0.005, 0.001)
    out['level'] = 'warning' if spread >= lim_warn else ('notice' if spread >= lim_note else 'ok')
    return out


def _split_by_speed(cl, rtol):
    """Split a cluster into groups separated by gaps in rotor speed larger than rtol."""
    s = sorted(cl, key=lambda h: h.header.get('rotor_speed', 0.0))
    out = [[s[0]]]
    for h in s[1:]:
        w0, w = out[-1][-1].header.get('rotor_speed', 0.0), h.header.get('rotor_speed', 0.0)
        if abs(w - w0) > rtol * max(abs(w0), 1e-3):
            out.append([h])
        else:
            out[-1].append(h)
    return out


def find_lin_files(folder: str, recursive: bool = False) -> List[str]:
    out = []
    if recursive:
        for dp, _, fns in os.walk(folder):
            out += [os.path.join(dp, f) for f in fns if f.lower().endswith('.lin')]
    else:
        out = [os.path.join(folder, f) for f in os.listdir(folder) if f.lower().endswith('.lin')]
        if not out:  # common layout: <case>/linearizations/*.lin one level down
            for d in sorted(os.listdir(folder)):
                p = os.path.join(folder, d)
                if os.path.isdir(p):
                    out += [os.path.join(p, f) for f in os.listdir(p) if f.lower().endswith('.lin')]
    return out


def _root(path):
    m = _ROOT_RE.match(os.path.basename(path))
    return m.group(1) if m else os.path.basename(path)


def group_operating_points(files: List[str], wind_tol: float = 1e-3, speed_rtol: float = 0.02):
    """Return (ops, global_warnings, skipped)."""
    heads, skipped = [], []
    for f, h in zip(files, read_many(files)):       # headers in parallel, one open per file
        if isinstance(h, Exception):                 # unreadable file
            skipped.append((os.path.basename(f), str(h)))
            continue
        if 'nx' not in h.header:
            skipped.append((os.path.basename(f), 'not an OpenFAST linearisation file'))
            continue
        heads.append(h)
    warnings = []
    no_wind = [h for h in heads if not math.isfinite(h.header.get('wind_speed', float('nan')))]
    if no_wind:
        warnings.append(f'{len(no_wind)} file(s) have no wind speed in the header; grouped by file-name root.')
    # 1) cluster by wind speed only; 2) a wind speed with duplicated azimuths holds more than one
    #    set: split by file root, then by gaps in rotor speed; 3) otherwise keep it whole
    heads.sort(key=lambda h: (h.header.get('wind_speed', float('inf')) if math.isfinite(
        h.header.get('wind_speed', float('nan'))) else float('inf'), h.header.get('rotor_speed', 0.0)))
    clusters: List[List] = []
    for h in heads:
        U = h.header.get('wind_speed', float('nan'))
        placed = False
        for cl in clusters:
            U0 = cl[0].header.get('wind_speed', float('nan'))
            if math.isfinite(U) and math.isfinite(U0):
                sameU = abs(U - U0) <= wind_tol * max(1.0, abs(U0))
            else:
                sameU = (not math.isfinite(U)) and (not math.isfinite(U0)) and _root(h.path) == _root(cl[0].path)
            if sameU:
                cl.append(h)
                placed = True
                break
        if not placed:
            clusters.append([h])

    def dup_az(cl):
        az = [round(math.degrees(h.azimuth) % 360, 1) for h in cl if math.isfinite(h.azimuth)]
        return len(set(az)) < len(az)

    final = []
    for cl in clusters:
        if not dup_az(cl):
            final.append(cl)
            continue
        roots = {}
        for h in cl:
            roots.setdefault(_root(h.path), []).append(h)
        parts = list(roots.values()) if len(roots) > 1 else [cl]
        how = 'file-name root' if len(roots) > 1 else ''
        if any(dup_az(p) for p in parts):                      # still duplicated: separate by rotor speed
            parts = [q for p in parts for q in _split_by_speed(p, speed_rtol)]
            how = (how + ' and ' if how else '') + 'rotor speed'
        final += parts
        if len(parts) > 1:
            warnings.append(f'{len(parts)} linearisation sets share wind speed {cl[0].wind_speed:g} m/s '
                            f'(azimuths repeat); kept separate by {how}.')
    ops = []
    for cl in final:
        cl.sort(key=lambda h: (h.azimuth % (2 * math.pi)) if math.isfinite(h.azimuth) else 0.0)
        g = OpGroup(wind_speed=cl[0].header.get('wind_speed', float('nan')),
                    rotor_speed=sum(h.header.get('rotor_speed', 0.0) for h in cl) / len(cl),
                    files=[h.path for h in cl], azimuths=[h.azimuth for h in cl],
                    roots=[_root(h.path) for h in cl],
                    speeds=[h.header.get('rotor_speed', float('nan')) for h in cl],
                    times=[h.header.get('time', float('nan')) for h in cl])
        g.omega_check = rotor_speed_check(g.speeds, g.azimuths, g.times)
        if g.omega_check['level'] != 'ok':
            g.warnings.append(('⚠ ' if g.omega_check['level'] == 'warning' else '') + g.omega_check['message'])
        if dup_az(cl):
            g.warnings.append('duplicate azimuths (averaging counts them twice)')
        nx = {int(h.header['nx']) for h in cl}
        if len(nx) > 1:
            g.warnings.append(f'state count differs between azimuths: {sorted(nx)}')
        azd = sorted(math.degrees(a) % 360 for a in g.azimuths)
        if len(azd) > 1:
            gaps = [(azd[(k + 1) % len(azd)] - azd[k]) % 360 for k in range(len(azd))]
            if max(gaps) - min(gaps) > 5.0:
                g.warnings.append('azimuths are not evenly spaced (averaging is biased)')
        elif len(azd) == 1:
            g.warnings.append('single azimuth: MBC applied, no azimuth averaging')
        if len(set(g.roots)) > 1:
            g.warnings.append('files from different name roots grouped by header values')
        ops.append(g)
    ops.sort(key=lambda g: (g.wind_speed if math.isfinite(g.wind_speed) else float('inf'), g.rotor_speed))
    return ops, warnings, skipped


def scan_folder(folder: str, recursive: bool = False, use_cache: bool = False):
    """Returns (info dict, list of OpGroup).  use_cache: reuse the scan stored on disk when no
    .lin file changed (the app does; tests and scripts get a fresh scan)."""
    if not os.path.isdir(folder):
        raise FileNotFoundError(f'Folder not found: {folder}')
    files = find_lin_files(folder, recursive)
    k = cache.key('scan', files, recursive) if use_cache else None
    if k:
        hit = cache.load(k)
        if hit is not None:
            return hit
    ops, warnings, skipped = group_operating_points(files)
    naming_sorted = sorted(files)
    content_order = [f for g in ops for f in g.files]
    reordered = naming_sorted != content_order and len(files) > 1
    out = dict(folder=os.path.abspath(folder), n_files=len(files),
               ops=[g.to_dict() for g in ops], warnings=warnings,
               skipped=[dict(file=a, reason=b) for a, b in skipped],
               name_order_differs=reordered), ops
    if k:
        cache.save(k, out)
    return out
