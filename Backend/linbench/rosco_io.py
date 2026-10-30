"""Readers for ROSCO / OpenFAST text inputs: DISCON.IN, tuning YAML, Cp-Ct-Cq table,
and the handful of OpenFAST input-file keys needed for the 1-DOF design model."""
from __future__ import annotations

import os
import re
from typing import Dict

import numpy as np

try:
    import yaml
except ImportError:  # pragma: no cover
    yaml = None


def _parse_value(txt: str):
    txt = txt.strip()
    if not txt:
        return None
    if txt[0] in '"\'':
        q = txt[0]
        end = txt.find(q, 1)
        return txt[1:end] if end > 0 else txt[1:]
    toks = txt.split()
    vals = []
    for t in toks:
        try:
            v = float(t.replace('D', 'E').replace('d', 'e'))
            vals.append(int(v) if re.fullmatch(r'[+-]?\d+', t) else v)
        except ValueError:
            if t.lower() in ('true', 'false'):
                vals.append(t.lower() == 'true')
            else:
                vals.append(t)
    return vals[0] if len(vals) == 1 else vals


def read_discon(path: str) -> Dict:
    """DISCON.IN -> {name: value}.  Lines look like  'values   ! Name   - comment'."""
    out = {}
    with open(path, 'r', errors='replace') as fh:
        for line in fh:
            if '!' not in line:
                continue
            val, rest = line.split('!', 1)
            if not val.strip():
                continue
            m = re.match(r'\s*([A-Za-z_][A-Za-z0-9_]*)', rest)
            if not m:
                continue
            out[m.group(1)] = _parse_value(val)
    out['_path'] = os.path.abspath(path)
    return out


def as_list(v):
    if v is None:
        return []
    if isinstance(v, (list, tuple, np.ndarray)):
        return [float(x) for x in v]
    return [float(v)]


def read_openfast_keys(path: str) -> Dict:
    """Generic OpenFAST input reader: 'value  Key  - comment' -> {Key: value}."""
    out = {}
    with open(path, 'r', errors='replace') as fh:
        for line in fh:
            s = line.strip()
            if not s or s.startswith(('-', '=', '!', '#')):
                continue
            m = re.match(r'^("[^"]*"|\S+)\s+([A-Za-z_][A-Za-z0-9_()]*)', s)
            if not m:
                continue
            out.setdefault(m.group(2), _parse_value(m.group(1)))
    return out


def read_fast_params(fst_path: str) -> Dict:
    """Pull TipRad, HubRad, GBRatio, GBoxEff, GenIner, TowerHt, GenEff, AirDens out of an OpenFAST model."""
    res = {}
    if not fst_path or not os.path.isfile(fst_path):
        return res
    base = os.path.dirname(fst_path)
    fst = read_openfast_keys(fst_path)
    if isinstance(fst.get('AirDens'), (int, float)):
        res['AirDens'] = float(fst['AirDens'])

    def sub(key):
        v = fst.get(key)
        if isinstance(v, str) and v.lower() not in ('unused', 'none'):
            p = os.path.join(base, v)
            if os.path.isfile(p):
                return read_openfast_keys(p)
        return {}
    ed, srv, ad = sub('EDFile'), sub('ServoFile'), sub('AeroFile')
    for k in ('TipRad', 'HubRad', 'GBRatio', 'GBoxEff', 'GenIner', 'TowerHt', 'NumBl', 'HubIner'):
        if isinstance(ed.get(k), (int, float)):
            res[k] = float(ed[k])
    if isinstance(srv.get('GenEff'), (int, float)):
        res['GenEff'] = float(srv['GenEff'])
    if 'AirDens' not in res and isinstance(ad.get('AirDens'), (int, float)):
        res['AirDens'] = float(ad['AirDens'])
    return res


def read_yaml(path: str) -> Dict:
    if yaml is None:
        raise RuntimeError('PyYAML is not installed')
    with open(path, 'r') as fh:
        d = yaml.safe_load(fh) or {}
    d['_path'] = os.path.abspath(path)
    return d


def read_cp_table(path: str):
    """ROSCO Cp_Ct_Cq text format -> dict(pitch_rad, tsr, Cp, Ct, Cq) with tables [n_tsr x n_pitch]."""
    pitch = tsr = None
    tabs = {}
    with open(path, 'r') as fh:
        lines = fh.read().splitlines()
    i = 0
    while i < len(lines):
        s = lines[i]
        if 'Pitch angle' in s:
            pitch = np.array(lines[i + 1].split(), float)
            i += 2
            continue
        if 'TSR' in s and 'vector' in s:
            tsr = np.array(lines[i + 1].split(), float)
            i += 2
            continue
        for key, name in (('Power', 'Cp'), ('Thrust', 'Ct'), ('Torque', 'Cq')):
            if key in s and s.lstrip().startswith('#') and tsr is not None:
                j = i + 1
                while j < len(lines) and not lines[j].strip():
                    j += 1
                rows = [np.array(lines[j + k].split(), float) for k in range(len(tsr))]
                tabs[name] = np.array(rows)
                i = j + len(tsr) - 1
                break
        i += 1
    if pitch is None or tsr is None or 'Cp' not in tabs:
        raise ValueError(f'{path}: not a ROSCO Cp-Ct-Cq table')
    return dict(pitch_rad=np.radians(pitch), tsr=tsr, **tabs, _path=os.path.abspath(path))


def resolve(path_like, base_dir):
    if not path_like or not isinstance(path_like, str):
        return None
    p = path_like if os.path.isabs(path_like) else os.path.join(base_dir, path_like)
    p = os.path.normpath(p)
    return p if os.path.exists(p) else None


def discover_from_yaml(yaml_path: str) -> Dict:
    """Paths a tuning YAML points to: FAST model, Cp table, lin folder."""
    d = read_yaml(yaml_path)
    base = os.path.dirname(os.path.abspath(yaml_path))
    pp = d.get('path_params', {}) or {}
    fast_dir = resolve(pp.get('FAST_directory'), base)
    fst = resolve(os.path.join(pp.get('FAST_directory', ''), pp.get('FAST_InputFile', '')), base) \
        if pp.get('FAST_InputFile') else None
    cp = resolve(pp.get('rotor_performance_filename'), base)
    lm = d.get('linmodel_tuning', {}) or {}
    lin = resolve(lm.get('linfile_path'), base)
    discon = None
    if fast_dir:
        for f in os.listdir(fast_dir):
            if f.lower().endswith('.in') and 'discon' in f.lower():
                discon = os.path.join(fast_dir, f)
                break
    return dict(yaml=d, fst=fst, cp=cp, lin=lin, fast_dir=fast_dir, discon=discon)
