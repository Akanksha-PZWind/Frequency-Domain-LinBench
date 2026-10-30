"""Local JSON API for the LinBench WPF app (127.0.0.1 only): folder scan, analysis, Operating points, Campbell,
mode-shape animation."""
from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from . import __version__, offline, paths

# FAST START: numpy / scipy (inside .analysis) take seconds to import on Windows. They are NOT imported here,
# so the server answers /api/ping at once; a background thread imports them right after start (warm_up),
# and the first request that needs them simply waits for that import to finish.
STATE_DIR = paths.settings_dir()
RECENT = os.path.join(STATE_DIR, 'recent.json')
_SESSION = None
_SESSION_LOCK = threading.Lock()
IDLE = dict(stage='idle', pct=0, msg='', done=False, error=None, log=[])


def session():
    """The analysis session (imports numpy / scipy on first use)."""
    global _SESSION
    with _SESSION_LOCK:
        if _SESSION is None:
            from .analysis import Session
            _SESSION = Session()
        return _SESSION


def clean(obj):
    """JSON-safe values; uses the numpy-aware version once the analysis code is loaded."""
    if _SESSION is None:                      # analysis code not (fully) loaded yet: plain Python values only
        return obj
    from .analysis import clean as _clean
    return _clean(obj)


_WARM = {'started': False}
_WARM_LOCK = threading.Lock()


def warm_up():
    """Import the analysis code (numpy, scipy) in the background. Started once, after the first /api/ping has
    been answered (so that answer is not slowed down by the import), or 3 s after start at the latest."""
    with _WARM_LOCK:
        if _WARM['started']:
            return
        _WARM['started'] = True

    def run():
        try:
            session()
            print('LinBench analysis engine loaded (numpy, scipy)', flush=True)
        except Exception:
            traceback.print_exc()
    threading.Thread(target=run, daemon=True).start()


def _recent_load():
    try:
        with open(RECENT) as fh:
            return json.load(fh)
    except Exception:
        return []


def _recent_save(cfg):
    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        lst = [c for c in _recent_load() if c.get('lin_dir') != cfg.get('lin_dir')]
        lst.insert(0, {'lin_dir': cfg.get('lin_dir', '')})
        with open(RECENT, 'w') as fh:
            json.dump(lst[:8], fh, indent=1)
    except Exception:
        pass


class Handler(BaseHTTPRequestHandler):
    server_version = 'LinBench'

    def log_message(self, fmt, *args):  # quiet
        pass

    # ---------------------------------------------------------------- helpers
    def _json(self, obj, code=200):
        body = json.dumps(clean(obj), separators=(',', ':')).encode()
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        n = int(self.headers.get('Content-Length', 0) or 0)
        if not n:
            return {}
        return json.loads(self.rfile.read(n).decode() or '{}')

    # ---------------------------------------------------------------- routes
    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        try:
            if u.path == '/api/ping':
                self._json({'ok': True, 'app': 'LinBench', 'version': __version__})
                warm_up()                               # the answer is sent: now load numpy / scipy
                return
            if u.path == '/api/version':
                return self._json({'version': __version__, 'frozen': paths.FROZEN, 'offline': offline.offline_works(),
                                   'blocked': len(offline.BLOCKED)})
            if u.path == '/api/progress':
                p = dict(_SESSION.progress if _SESSION is not None else IDLE)
                log = p.get('log', [])
                k = int(q.get('since', 0) or 0)
                p.update(log=log[k:], log_n=len(log))
                return self._json(p)
            if u.path == '/api/summary':
                return self._json((_SESSION.summary if _SESSION is not None else None) or {})
            if u.path == '/api/recent':
                return self._json(_recent_load())
            if u.path == '/api/campbell':
                return self._json(session().campbell_data(q.get('all') == '1'))
            return self._json(dict(error='unknown endpoint'), 404)
        except Exception as e:
            return self._json(dict(error=f'{type(e).__name__}: {e}', trace=traceback.format_exc()), 500)

    def do_POST(self):
        u = urlparse(self.path)
        try:
            b = self._body()
            if u.path == '/api/open_folder':           # the status bar's folder: open it in Explorer
                p = b.get('path', '')
                if not p or not os.path.isdir(p):
                    return self._json(dict(error='Folder not found.'), 400)
                if os.name == 'nt':
                    os.startfile(p)  # noqa: S606 - a folder the user analysed, opened in Explorer
                else:
                    subprocess.Popen(['open' if sys.platform == 'darwin' else 'xdg-open', p])
                return self._json({'ok': True})
            if u.path == '/api/inspect':
                return self._json(session().inspect(b))
            if u.path == '/api/mode_shape':          # Campbell visualization: animate one clicked mode
                opt = lambda k_: float(b[k_]) if b.get(k_) not in (None, '') else None
                opti = lambda k_: int(b[k_]) if b.get(k_) not in (None, '') else None
                return self._json(session().mode_shape(
                    opti('op'), opti('k'), float(b.get('scale') or 0.15), int(b.get('n_frames') or 48),
                    opt('tower_len'), opt('blade_len'), opt('hub_radius'),
                    wind=opt('wind'), mode_index=opti('mode_index'), freq=opt('freq'),
                    include_all=bool(b.get('include_all'))))
            if u.path == '/api/load':
                if not b.get('lin_dir') or not os.path.isdir(b['lin_dir']):
                    return self._json(dict(error='Linearisation folder not found.'), 400)
                _recent_save(b)
                b.setdefault('cache', True)          # the app reuses results stored on disk; scripts/tests do not
                session().start_load(b)
                return self._json(dict(started=True))
            return self._json(dict(error='unknown endpoint'), 404)
        except Exception as e:
            return self._json(dict(error=f'{type(e).__name__}: {e}', trace=traceback.format_exc()), 500)


def serve(port=8765, host='127.0.0.1'):
    """Serve the API on this PC; the first free port from `port` on is used (the WPF app follows it)."""
    httpd = None
    for p in range(port, port + 20):
        try:
            httpd = ThreadingHTTPServer((host, p), Handler)
            port = p
            break
        except OSError:
            continue
    if httpd is None:
        raise SystemExit('No free port found')
    httpd.daemon_threads = True
    print(f'LinBench running at http://{host}:{port}/  (close this window to stop)', flush=True)
    threading.Timer(3.0, warm_up).start()           # in case nobody pings (manual start with start_backend.bat)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    return port
