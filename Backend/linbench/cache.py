"""Results kept on disk between runs, so a folder that was analysed before reopens at once.

What is stored (pickle, in ~/.linbench/cache): the scan of a folder, and the processed
operating-point models with their modes and Campbell tracks.  A stored result is used only when
every .lin file still has the same name, size and modification time, the options are the same and
the LinBench code is the same; anything else gives a different key, so stale results are never read.
The newest KEEP entries are kept.  Delete the folder to clear it.  The files are written and read
only on this PC, by this program.
"""
from __future__ import annotations

import hashlib
import os
import pickle
import sys

from . import __version__

DIR = os.path.join(os.path.expanduser('~'), '.linbench', 'cache')
KEEP = 16
FORMAT = 3          # bump when the stored objects change shape


def _code_stamp():
    """Changes whenever the engine changes (source mtimes when run from source, version when frozen)."""
    if getattr(sys, 'frozen', False):
        return __version__
    here = os.path.dirname(os.path.abspath(__file__))
    try:
        return str(max(os.stat(os.path.join(here, f)).st_mtime_ns for f in os.listdir(here) if f.endswith('.py')))
    except OSError:
        return __version__


def key(kind: str, files, *options) -> str:
    h = hashlib.sha1()
    h.update(f'{FORMAT}|{kind}|{_code_stamp()}|{options!r}'.encode())
    for f in sorted(files):
        try:
            st = os.stat(f)
            h.update(f'{os.path.abspath(f)}|{st.st_mtime_ns}|{st.st_size}\n'.encode())
        except OSError:
            h.update(f'{f}|missing\n'.encode())
    return f'{kind}-{h.hexdigest()[:24]}'


def load(k: str):
    p = os.path.join(DIR, k + '.pkl')
    try:
        with open(p, 'rb') as fh:
            obj = pickle.load(fh)
        os.utime(p, None)               # most recently used
        return obj
    except Exception:                   # missing, truncated or from an incompatible build: recompute
        return None


def save(k: str, obj) -> None:
    try:
        os.makedirs(DIR, exist_ok=True)
        tmp = os.path.join(DIR, k + '.tmp')
        with open(tmp, 'wb') as fh:
            pickle.dump(obj, fh, protocol=pickle.HIGHEST_PROTOCOL)
        os.replace(tmp, os.path.join(DIR, k + '.pkl'))
        ents = sorted((e for e in os.scandir(DIR) if e.name.endswith('.pkl')), key=lambda e: e.stat().st_mtime, reverse=True)
        for e in ents[KEEP:]:
            os.remove(e.path)
    except Exception:                   # a cache that cannot be written must never stop an analysis
        pass
