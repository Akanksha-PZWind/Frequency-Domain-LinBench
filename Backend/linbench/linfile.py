"""Reader for OpenFAST linearisation (.lin) files.

Everything is identified by label, never by position: the header block is read by
field name, the channel tables by their section titles, and the matrices by their
"X: n x m" banners.  Works with OpenFAST 2.x files (no "Derivative Order" column)
and 3.x/4.x files, with or without the optional Jacobian blocks.
"""
from __future__ import annotations

import os
import re
import threading
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from typing import Dict, List, Optional

import numpy as np

_HEADER_FIELDS = {
    'simulation time': 'time',
    'rotor speed': 'rotor_speed',
    'azimuth': 'azimuth',
    'wind speed': 'wind_speed',
    'number of continuous states': 'nx',
    'number of discrete states': 'nxd',
    'number of constraint states': 'nz',
    'number of inputs': 'nu',
    'number of outputs': 'ny',
}
_SECTION_TITLES = {
    'order of continuous states': 'x',
    'order of continuous state derivatives': 'dx',
    'order of inputs': 'u',
    'order of outputs': 'y',
}
_MATRIX_RE = re.compile(r'^\s*([A-Za-z][A-Za-z0-9_]*)\s*:\s*(\d+)\s*x\s*(\d+)\s*$')
_ROW_RE = re.compile(r'^\s*(\d+)\s+(.+?)\s+([TF])\s+(?:(\d)\s+)?(\S.*)$')
_UNIT_PAREN = re.compile(r'\(([^()]*)\)\s*$')
_UNIT_COMMA = re.compile(r',\s*([^,]+?)\s*$')


def _num(tok: str) -> float:
    try:
        return float(tok)
    except ValueError:
        t = tok.strip().lower()
        if t in ('nan', '-nan'):
            return float('nan')
        if 'inf' in t:
            return float('-inf') if t.startswith('-') else float('inf')
        raise


@dataclass
class Channel:
    """One row of a state / input / output table."""
    index: int                  # 0-based position in its table
    op: float                   # operating-point value (first value if a list)
    rotating: bool              # "Rotating Frame?" flag
    deriv_order: int            # 0 = algebraic/output, 1 = first-order, 2 = second-order state
    desc: str                   # full description as written in the file
    module: str = ''            # ED, BD_1, AD, IfW, SrvD, HD, MAP ...
    unit: str = ''              # parsed unit string ('rpm', 'rad', 'm/s^2', ...)
    name: str = ''              # description with module prefix and unit removed

    def to_dict(self):
        return dict(i=self.index, op=self.op, rot=self.rotating, order=self.deriv_order,
                    desc=self.desc, module=self.module, unit=self.unit, name=self.name)


def _parse_channel(line: str, idx: int) -> Optional[Channel]:
    m = _ROW_RE.match(line)
    if not m:
        return None
    op_txt = m.group(2).split(',')[0].strip()
    try:
        op = _num(op_txt)
    except ValueError:
        op = float('nan')
    desc = m.group(5).strip()
    module = desc.split()[0] if desc else ''
    unit = ''
    mu = _UNIT_PAREN.search(desc)
    if mu:
        unit = mu.group(1).strip()
        body = desc[:mu.start()].rstrip(', ')
    else:
        mc = _UNIT_COMMA.search(desc)
        if mc and len(mc.group(1)) <= 12 and ' ' not in mc.group(1).strip():
            unit = mc.group(1).strip()
            body = desc[:mc.start()]
        else:
            body = desc
    body = re.sub(r'\s*\(internal DOF index = [^()]*(?:\([^()]*\))?[^()]*\)\s*', ' ', body).strip()
    name = body[len(module):].strip() if body.startswith(module) else body
    order = int(m.group(4)) if m.group(4) is not None else -1
    return Channel(idx, op, m.group(3) == 'T', order, desc, module, unit, name)


@dataclass
class LinFile:
    path: str
    header: Dict[str, float] = field(default_factory=dict)
    title: str = ''
    modules: str = ''
    x: List[Channel] = field(default_factory=list)
    dx: List[Channel] = field(default_factory=list)
    u: List[Channel] = field(default_factory=list)
    y: List[Channel] = field(default_factory=list)
    mats: Dict[str, np.ndarray] = field(default_factory=dict)

    # convenient accessors ------------------------------------------------
    @property
    def A(self):
        return self.mats.get('A')

    @property
    def B(self):
        return self.mats.get('B')

    @property
    def C(self):
        return self.mats.get('C')

    @property
    def D(self):
        return self.mats.get('D')

    @property
    def wind_speed(self):
        return self.header.get('wind_speed', float('nan'))

    @property
    def rotor_speed(self):
        return self.header.get('rotor_speed', float('nan'))

    @property
    def azimuth(self):
        return self.header.get('azimuth', float('nan'))


def _read_header(lines, lf: LinFile):
    for k, raw in enumerate(lines[:40]):
        s = raw.strip()
        low = s.lower()
        if 'linked with' in low:
            lf.modules = s.split('linked with', 1)[1].strip()
        if low.startswith('description from the fast input file'):
            lf.title = s.split(':', 1)[1].strip() if ':' in s else ''
        if ':' not in s:
            continue
        key, val = s.split(':', 1)
        key = key.strip().lower()
        if key in _HEADER_FIELDS:
            tok = val.split()
            if tok:
                try:
                    lf.header[_HEADER_FIELDS[key]] = _num(tok[0])
                except ValueError:
                    pass


# Opening a file is the slow part on many PCs (antivirus / synced folders): ~18 ms per first open
# against ~6 ms to parse.  Headers and parsed files are therefore kept per (path, mtime, size), so
# the setup-screen scan and the analysis never open a file twice, and many files are opened in
# parallel threads (file I/O releases the GIL).
_HDR_CACHE: Dict[tuple, 'LinFile'] = {}
_LIN_CACHE: Dict[tuple, 'LinFile'] = {}
_CACHE_LOCK = threading.Lock()


def _stamp(path: str):
    st = os.stat(path)
    return (os.path.abspath(path), st.st_mtime_ns, st.st_size)


def read_header(path: str) -> LinFile:
    """Read only the header (fast; used when scanning a folder)."""
    key = _stamp(path)
    lf = _HDR_CACHE.get(key)
    if lf is not None:
        return lf
    lf = LinFile(path)
    with open(path, 'r', errors='replace') as fh:
        lines = []
        for _ in range(40):
            ln = fh.readline()
            if not ln:
                break
            lines.append(ln)
    _read_header(lines, lf)
    with _CACHE_LOCK:
        _HDR_CACHE[key] = lf
    return lf


def read_many(paths, full: bool = False, workers: int = 8):
    """Read headers (or whole files) of many paths in parallel.  Returns a list aligned with
    paths whose items are LinFile objects or the Exception raised for that file."""
    fn = read_lin if full else read_header

    def one(p):
        try:
            return fn(p)
        except Exception as e:          # reported per file by the caller
            return e
    if len(paths) < 4:
        return [one(p) for p in paths]
    with ThreadPoolExecutor(max_workers=workers) as ex:
        return list(ex.map(one, paths))


def clear_cache(full_only: bool = True):
    """Drop parsed files (they are large); headers are kept unless full_only is False."""
    with _CACHE_LOCK:
        _LIN_CACHE.clear()
        if not full_only:
            _HDR_CACHE.clear()


def read_lin(path: str) -> LinFile:
    """Read a full .lin file (header, channel tables, matrices).  The result is cached and shared:
    treat it as read-only."""
    key = _stamp(path)
    lf = _LIN_CACHE.get(key)
    if lf is None:
        lf = _read_lin(path)
        with _CACHE_LOCK:
            _LIN_CACHE[key] = lf
    return lf


def _read_lin(path: str) -> LinFile:
    with open(path, 'r', errors='replace') as fh:
        lines = fh.read().splitlines()
    lf = LinFile(path)
    _read_header(lines, lf)
    n = len(lines)
    i = 0
    while i < n:
        s = lines[i].strip()
        low = s.lower().rstrip(':')
        if low in _SECTION_TITLES:
            key = _SECTION_TITLES[low]
            chans: List[Channel] = []
            i += 1
            # skip column titles and the dashed ruler (at most a few lines)
            skipped = 0
            while i < n and skipped < 6 and _ROW_RE.match(lines[i]) is None:
                i += 1
                skipped += 1
            while i < n:
                ch = _parse_channel(lines[i], len(chans))
                if ch is None:
                    break
                chans.append(ch)
                i += 1
            setattr(lf, key, chans)
            continue
        m = _MATRIX_RE.match(lines[i])
        if m:
            name, r, c = m.group(1), int(m.group(2)), int(m.group(3))
            block = lines[i + 1:i + 1 + r]
            data = np.array(' '.join(block).replace('D', 'E').split(), dtype=float) if r * c else np.zeros(0)
            if data.size != r * c:
                raise ValueError(f'{path}: matrix {name} expected {r}x{c} values, found {data.size}')
            lf.mats[name] = data.reshape(r, c)
            i += 1 + r
            continue
        i += 1
    # consistency checks against the header counts
    nx = int(lf.header.get('nx', len(lf.x)))
    if lf.x and len(lf.x) != nx:
        raise ValueError(f'{path}: header says {nx} states, table lists {len(lf.x)}')
    if 'A' not in lf.mats and nx > 0:
        raise ValueError(f'{path}: no A matrix found')
    nu, ny = len(lf.u), len(lf.y)
    if 'B' not in lf.mats:
        lf.mats['B'] = np.zeros((nx, nu))
    if 'C' not in lf.mats:
        lf.mats['C'] = np.zeros((ny, nx))
    if 'D' not in lf.mats:
        lf.mats['D'] = np.zeros((ny, nu))
    # fill derivative order for OpenFAST 2.x files (column absent)
    if lf.x and lf.x[0].deriv_order < 0:
        _infer_state_order(lf.x)
    for tbl in (lf.u, lf.y):
        for ch in tbl:
            if ch.deriv_order < 0:
                ch.deriv_order = 0
    # fall back on operating-point values when the header lacks wind / rotor speed
    if not np.isfinite(lf.header.get('wind_speed', np.nan)):
        for ch in lf.u:
            if ch.module == 'IfW' and 'horizontal wind speed' in ch.desc.lower():
                lf.header['wind_speed'] = ch.op
                break
    return lf


def _infer_state_order(states: List[Channel]):
    """OpenFAST 2.x: ED/BD states come as [q ; q_dot] per module, all others first order."""
    by_mod: Dict[str, List[Channel]] = {}
    for ch in states:
        by_mod.setdefault(ch.module, []).append(ch)
    for mod, lst in by_mod.items():
        second = mod == 'ED' or mod.startswith('BD')
        for ch in lst:
            ch.deriv_order = 2 if second else 1
