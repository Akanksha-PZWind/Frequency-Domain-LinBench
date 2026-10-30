"""python -m linbench  [--port 8765] [--no-browser] [--browser] [--host 127.0.0.1]

LinBench.exe (and python -m linbench) opens LinBench in its own window (Qt WebEngine). --browser uses the default
browser instead, --no-browser only serves the page. --window-test <url> checks the window engine headlessly.
"""
import argparse
import os
import sys
import traceback


def _install_crash_log():
    """A windowed .exe has no console: unexpected errors go to ~/.linbench/linbench_error.log."""
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

    offline.stay_on_this_pc()            # the window, the engine and the file dialog: none of them leaves this PC
    if argv and argv[0] == '--pick':     # helper process for the native file dialog
        from .server import pick_dialog
        pick_dialog(argv[1] if len(argv) > 1 else 'dir', argv[2] if len(argv) > 2 else '')
        return 0
    if argv and argv[0] == '--window-test':
        from .window import selftest
        return selftest(argv[1] if len(argv) > 1 else 'http://127.0.0.1:8765/')
    _install_crash_log()
    ap = argparse.ArgumentParser(prog='linbench', description='Open/closed-loop analysis of OpenFAST linearisations')
    ap.add_argument('--port', type=int, default=8765)
    ap.add_argument('--no-browser', action='store_true', help='only serve the page (for development and tests)')
    ap.add_argument('--browser', action='store_true', help='open in the default browser instead of LinBench\'s own window')
    ap.add_argument('--host', default='127.0.0.1')
    a = ap.parse_args(argv)
    from .server import serve
    serve(a.port, not a.no_browser, a.host, browser=a.browser)
    return 0


if __name__ == '__main__':
    sys.exit(main())
