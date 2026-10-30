"""python -m linbench  [--port 8765] [--host 127.0.0.1]

Starts the LinBench backend for the WPF app: a JSON API on this PC only.
(--no-browser is accepted for compatibility with start_backend.bat and the app; there is no browser page.)
"""
import argparse
import os
import sys
import traceback


def _install_crash_log():
    """Unexpected errors also go to ~/.linbench/linbench_error.log."""
    from . import paths

    log_path = os.path.join(paths.settings_dir(), 'linbench_error.log')

    def hook(exc_type, exc, tb):
        try:
            with open(log_path, 'a', encoding='utf-8') as fh:
                fh.write(''.join(traceback.format_exception(exc_type, exc, tb)) + '\n')
        except OSError:
            pass
        sys.__excepthook__(exc_type, exc, tb)

    sys.excepthook = hook


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    from . import offline

    offline.stay_on_this_pc()            # the backend never leaves this PC
    _install_crash_log()
    ap = argparse.ArgumentParser(prog='linbench', description='Open-loop analysis of OpenFAST linearisations '
                                 '(operating points and Campbell diagram) for the LinBench WPF app')
    ap.add_argument('--port', type=int, default=8765)
    ap.add_argument('--host', default='127.0.0.1')
    ap.add_argument('--no-browser', action='store_true', help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    from .server import serve
    serve(a.port, a.host)
    return 0


if __name__ == '__main__':
    sys.exit(main())
