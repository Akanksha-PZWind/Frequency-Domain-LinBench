"""Where LinBench keeps its own files on this PC."""
import os
import sys

FROZEN = bool(getattr(sys, 'frozen', False))


def settings_dir():
    """LinBench's own files on this PC: cache, recent folders, the error log."""
    d = os.path.join(os.path.expanduser('~'), '.linbench')
    try:
        os.makedirs(d, exist_ok=True)
    except OSError:
        pass
    return d
