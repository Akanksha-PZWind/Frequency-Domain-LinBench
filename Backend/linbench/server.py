"""Local web server: serves the UI and a small JSON API around analysis.Session."""
from __future__ import annotations

import json
import mimetypes
import os
import string
import subprocess
import sys
import threading
import traceback
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from . import __version__, offline, paths
from .analysis import Session, clean

WEB = paths.web_dir()
STATE_DIR = paths.settings_dir()
RECENT = os.path.join(STATE_DIR, 'recent.json')
LOCK = os.path.join(STATE_DIR, 'server.json')        # the running instance: port and process id
SESSION = Session()
RAISE = {'n': 0}                                     # requests from a second start to bring the window to the front


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
        lst.insert(0, {k: cfg.get(k, '') for k in ('lin_dir', 'discon', 'yaml', 'cp', 'fst')})
        with open(RECENT, 'w') as fh:
            json.dump(lst[:8], fh, indent=1)
    except Exception:
        pass


def browse(path):
    """Directory listing for the in-app file browser."""
    if not path:
        if os.name == 'nt':
            drives = [f'{d}:\\' for d in string.ascii_uppercase if os.path.exists(f'{d}:\\')]
            return dict(path='', parent=None, dirs=[dict(name=d, path=d) for d in drives], files=[])
        path = os.path.expanduser('~')
    path = os.path.abspath(path)
    if os.path.isfile(path):
        path = os.path.dirname(path)
    dirs, files = [], []
    try:
        for e in sorted(os.scandir(path), key=lambda e: e.name.lower()):
            if e.name.startswith('.'):
                continue
            try:
                if e.is_dir():
                    dirs.append(dict(name=e.name, path=e.path))
                else:
                    files.append(dict(name=e.name, path=e.path, size=e.stat().st_size))
            except OSError:
                continue
    except OSError as err:
        return dict(path=path, parent=os.path.dirname(path), dirs=[], files=[], error=str(err))
    n_lin = sum(1 for f in files if f['name'].lower().endswith('.lin'))
    parent = os.path.dirname(path)
    if parent == path:
        parent = '' if os.name == 'nt' else None
    return dict(path=path, parent=parent, dirs=dirs, files=files[:400], n_lin=n_lin)


def native_pick(kind, start=''):
    """Open the operating system's own file/folder dialog in a helper process (tkinter)."""
    if getattr(sys, 'frozen', False):
        cmd = [sys.executable, '--pick', kind, start or '']
    else:
        cmd = [sys.executable, '-m', 'linbench', '--pick', kind, start or '']
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        p = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else ''
        if p == '__NO_TK__':
            return dict(path='', ok=False, error='no native dialog available')
        return dict(path=p, ok=True)
    except Exception as e:
        return dict(path='', ok=False, error=str(e))


def pick_dialog(kind, start=''):
    """Runs inside the helper process."""
    try:
        import tkinter as tk
        from tkinter import filedialog
    except Exception:
        print('__NO_TK__')
        return
    try:
        root = tk.Tk()
    except Exception:
        print('__NO_TK__')
        return
    root.withdraw()
    try:
        root.attributes('-topmost', True)
    except Exception:
        pass
    init = start if start and os.path.isdir(start) else (os.path.dirname(start) if start else os.path.expanduser('~'))
    if kind == 'dir':
        p = filedialog.askdirectory(parent=root, initialdir=init, title='Select the linearisation folder')
    else:
        types = {'discon': [('DISCON input', '*.IN *.in'), ('All files', '*.*')],
                 'yaml': [('ROSCO tuning YAML', '*.yaml *.yml'), ('All files', '*.*')],
                 'cp': [('Cp-Ct-Cq table', '*.txt'), ('All files', '*.*')],
                 'fst': [('OpenFAST model', '*.fst'), ('All files', '*.*')]}.get(kind, [('All files', '*.*')])
        p = filedialog.askopenfilename(parent=root, initialdir=init, filetypes=types, title='Select file')
    root.destroy()
    print(p or '')


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

    def _static(self, path):
        if path in ('', '/'):
            path = '/index.html'
        fp = os.path.normpath(os.path.join(WEB, path.lstrip('/')))
        if not fp.startswith(os.path.normpath(WEB)) or not os.path.isfile(fp):
            self.send_error(404)
            return
        with open(fp, 'rb') as fh:
            data = fh.read()
        self.send_response(200)
        self.send_header('Content-Type', mimetypes.guess_type(fp)[0] or 'application/octet-stream')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)

    # ---------------------------------------------------------------- routes
    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        try:
            if u.path == '/api/ping':
                return self._json({'ok': True, 'app': 'LinBench', 'version': __version__})
            if u.path == '/api/version':
                return self._json({'version': __version__, 'frozen': paths.FROZEN, 'offline': offline.offline_works(),
                                   'blocked': len(offline.BLOCKED)})
            if u.path == '/api/progress':
                p = dict(SESSION.progress)
                log = p.get('log', [])
                k = int(q.get('since', 0) or 0)
                p.update(log=log[k:], log_n=len(log))
                return self._json(p)
            if u.path == '/api/summary':
                return self._json(SESSION.summary or {})
            if u.path == '/api/recent':
                return self._json(_recent_load())
            if u.path == '/api/browse':
                return self._json(browse(q.get('path', '')))
            if u.path == '/api/modes':
                return self._json(SESSION.modes(int(q['op'])))
            if u.path == '/api/campbell':
                return self._json(SESSION.campbell_data(q.get('all') == '1', closed=q.get('closed') == '1'))
            if u.path == '/api/interaction':
                return self._json(SESSION.interaction())
            if u.path == '/api/delay':
                return self._json(SESSION.delay_margins(float(q.get('tau', 0))))
            if u.path == '/api/closed':
                return self._json(SESSION.closed(int(q['op'])))
            if u.path.startswith('/api/'):
                return self._json(dict(error='unknown endpoint'), 404)
            return self._static(u.path)
        except Exception as e:
            return self._json(dict(error=f'{type(e).__name__}: {e}', trace=traceback.format_exc()), 500)

    def do_POST(self):
        u = urlparse(self.path)
        try:
            b = self._body()
            if u.path == '/api/raise':                 # a second start of LinBench: show this window instead
                RAISE['n'] += 1
                return self._json({'ok': True})
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
                return self._json(Session.inspect(b))
            if u.path == '/api/pick':
                return self._json(native_pick(b.get('kind', 'dir'), b.get('start', '')))
            if u.path == '/api/load':
                if not b.get('lin_dir') or not os.path.isdir(b['lin_dir']):
                    return self._json(dict(error='Linearisation folder not found.'), 400)
                _recent_save(b)
                b.setdefault('cache', True)          # the app reuses results stored on disk; scripts/tests do not
                SESSION.start_load(b)
                return self._json(dict(started=True))
            if u.path == '/api/tf':
                return self._json(SESSION.tf(int(b['op']), int(b['input']), int(b['output'])))
            if u.path == '/api/time':
                return self._json(SESSION.time(int(b['op']), int(b['input']), int(b['output']), b.get('kind', 'step'),
                                               float(b.get('amp', 1.0)), float(b['T']) if b.get('T') else None))
            if u.path == '/api/sweep':
                return self._json(SESSION.sweep(int(b['input']), int(b['output'])))
            if u.path == '/api/structure':
                return self._json(SESSION.structure(int(b['op']), b.get('inputs', []), b.get('outputs', [])))
            if u.path == '/api/structure_sweep':
                return self._json(SESSION.structure_sweep(b.get('inputs', []), b.get('outputs', [])))
            if u.path == '/api/gain_map':
                return self._json(SESSION.gain_map(int(b['op']), b.get('pair', 'pi'), float(b.get('lo', 0.25)),
                                                   float(b.get('hi', 4.0)), int(b.get('n', 25))))
            if u.path == '/api/gain_point':
                return self._json(SESSION.gain_point(int(b['op']), float(b['fx']), float(b['fy']), b.get('pair', 'pi')))
            if u.path == '/api/gain_scaling':
                return self._json(SESSION.gain_scaling())
            if u.path == '/api/closed_time':
                return self._json(SESSION.closed_time(int(b['op']), int(b['input']), int(b['output']),
                                                      b.get('kind', 'step'), float(b.get('amp', 1.0)),
                                                      float(b['T']) if b.get('T') else None,
                                                      b.get('out_kind', 'plant')))
            if u.path == '/api/closed_tf':
                return self._json(SESSION.closed_tf(int(b['op']), int(b['input']), int(b['output'])))
            if u.path == '/api/region':
                return self._json(SESSION.set_region(int(b['op']), b.get('region', 'auto')))
            return self._json(dict(error='unknown endpoint'), 404)
        except Exception as e:
            return self._json(dict(error=f'{type(e).__name__}: {e}', trace=traceback.format_exc()), 500)


def existing_instance():
    """URL of a LinBench that is already running on this PC (it answers /api/ping), or None."""
    try:
        with open(LOCK) as fh:
            info = json.load(fh)
        import urllib.request

        with urllib.request.urlopen(f"http://127.0.0.1:{int(info['port'])}/api/ping", timeout=1.5) as r:
            if r.status == 200 and json.loads(r.read().decode()).get('app') == 'LinBench':
                return f"http://127.0.0.1:{int(info['port'])}/"
    except Exception:
        return None
    return None


def _ask_to_raise(url):
    try:
        import urllib.request

        req = urllib.request.Request(url + 'api/raise', data=b'{}', headers={'Content-Type': 'application/json'})
        urllib.request.urlopen(req, timeout=1.5).read()
    except Exception:
        pass


def serve(port=8765, open_browser=True, host='127.0.0.1', browser=False):
    """Serve LinBench on this PC.  open_browser: show it - in LinBench's own window (Qt WebEngine) when that is
    available and browser is False, otherwise in the default browser.  With the own window, closing the window
    stops LinBench; a second start brings the open window to the front instead of starting again."""
    use_window = open_browser and not browser
    if use_window:
        from . import window
        use_window = window.available()
    if open_browser:
        url = existing_instance()
        if url:
            if use_window:
                _ask_to_raise(url)
            else:
                webbrowser.open(url)
            print(f'LinBench is already running at {url}')
            return url
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
    url = f'http://{host}:{port}/'
    if open_browser:
        try:
            with open(LOCK, 'w') as fh:
                json.dump({'port': port, 'pid': os.getpid()}, fh)
        except OSError:
            pass
    try:
        serving = False
        if use_window:
            from . import window
            threading.Thread(target=httpd.serve_forever, daemon=True).start()
            serving = True
            print(f'LinBench running at {url} in its own window (close the window to stop)', flush=True)
            seen = {'n': RAISE['n']}

            def should_raise():
                if RAISE['n'] != seen['n']:
                    seen['n'] = RAISE['n']
                    return True
                return False
            try:
                window.run(url, should_raise)
            except Exception:       # the window engine cannot start on this PC: the page opens in the browser
                traceback.print_exc()
            else:
                httpd.shutdown()
                return url
        print(f'LinBench running at {url}  (close this window to stop)', flush=True)
        if open_browser:
            threading.Timer(0.8, lambda: webbrowser.open(url)).start()
        try:
            if serving:
                threading.Event().wait()
            else:
                httpd.serve_forever()
        except KeyboardInterrupt:
            pass
    finally:
        try:
            with open(LOCK) as fh:
                if json.load(fh).get('pid') == os.getpid():
                    fh.close()
                    os.remove(LOCK)
        except Exception:
            pass
    return url
