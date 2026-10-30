"""Where LinBench keeps its own files, and where its bundled files are (from source or inside the .exe)."""
import os
import sys

FROZEN = bool(getattr(sys, 'frozen', False))


def app_dir():
    """Folder of the program: the TuneMe-style build folder (LinBench.exe and _internal) or the source tree."""
    if FROZEN:
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def bundle_dir():
    """Folder the bundled data (web pages, icon) are in: _internal of the build, or the source tree."""
    if FROZEN:
        return getattr(sys, '_MEIPASS', os.path.dirname(sys.executable))
    return app_dir()


def settings_dir():
    """LinBench's own files on this PC: cache, recent folders, the running-instance note, the error log."""
    d = os.path.join(os.path.expanduser('~'), '.linbench')
    try:
        os.makedirs(d, exist_ok=True)
    except OSError:
        pass
    return d


def asset(name):
    return os.path.join(bundle_dir(), 'assets', name)


def web_dir():
    return os.path.join(bundle_dir(), 'linbench', 'web')
