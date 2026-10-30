"""LinBench's own window (same approach as TuneMe).

LinBench's screens are a web page served by LinBench itself on this PC (127.0.0.1). This module shows that page in
a window of LinBench's own, drawn by Qt WebEngine - a Chromium engine that travels inside the LinBench folder - so
no browser is needed. The window opens maximised, carries LinBench's name and icon, asks where exported files and
reports go, and closing it ends LinBench.

When PySide6 is not installed (running from source without the build packages) LinBench opens its page in the
default browser instead, as before.
"""
import os
import sys

from . import paths


def _qt():
    """The Qt classes the window needs, imported on first use (PySide6 with its WebEngine)."""
    from PySide6.QtCore import QTimer, QUrl
    from PySide6.QtGui import QIcon
    from PySide6.QtWebEngineCore import QWebEngineProfile, QWebEnginePage, QWebEngineSettings
    from PySide6.QtWebEngineWidgets import QWebEngineView
    from PySide6.QtWidgets import QApplication, QFileDialog, QMainWindow

    return dict(QTimer=QTimer, QUrl=QUrl, QIcon=QIcon, QWebEngineProfile=QWebEngineProfile, QWebEnginePage=QWebEnginePage,
                QWebEngineSettings=QWebEngineSettings, QWebEngineView=QWebEngineView, QApplication=QApplication,
                QFileDialog=QFileDialog, QMainWindow=QMainWindow)


def available():
    """True if the built-in window can be used here: the engine is part of this build (or installed) and imports."""
    try:
        _qt()
        return True
    except Exception:
        return False


def _app(argv=None):
    from PySide6.QtWidgets import QApplication

    app = QApplication.instance()
    if app is None:
        # nothing of the window is kept on disk: no browser profile, no cache (see run)
        os.environ.setdefault('QTWEBENGINE_CHROMIUM_FLAGS', '--disable-logging')
        app = QApplication(argv or [sys.argv[0]])
        app.setApplicationName('LinBench')
        app.setApplicationDisplayName('LinBench')
    return app


def _downloads_dir():
    d = os.path.join(os.path.expanduser('~'), 'Downloads')
    return d if os.path.isdir(d) else os.path.expanduser('~')


def run(url, should_raise=None):
    """Show LinBench's page at ``url`` in LinBench's own window and return when the window is closed.

    ``should_raise`` is asked a few times a second whether the window is wanted in front (LinBench was started a
    second time); it returns True once for every such request."""
    Q = _qt()
    app = _app()
    icon = paths.asset('linbench.ico')
    if os.path.exists(icon):
        app.setWindowIcon(Q['QIcon'](icon))

    # a profile that keeps nothing on disk: the page needs no cookies, history or cache between runs
    profile = Q['QWebEngineProfile'](app)
    profile.setHttpCacheType(Q['QWebEngineProfile'].HttpCacheType.MemoryHttpCache)
    profile.setPersistentCookiesPolicy(Q['QWebEngineProfile'].PersistentCookiesPolicy.NoPersistentCookies)
    page = Q['QWebEnginePage'](profile, app)
    s = page.settings()
    for attr, on in (('JavascriptCanOpenWindows', False), ('LocalStorageEnabled', True), ('PluginsEnabled', False),
                     ('PdfViewerEnabled', False), ('FullScreenSupportEnabled', False)):
        try:
            s.setAttribute(getattr(Q['QWebEngineSettings'].WebAttribute, attr), on)
        except AttributeError:
            pass

    win = Q['QMainWindow']()
    view = Q['QWebEngineView'](win)
    view.setPage(page)
    win.setCentralWidget(view)
    win.setWindowTitle('LinBench')
    view.titleChanged.connect(lambda t: win.setWindowTitle(t if t and t != 'LinBench' else 'LinBench'))

    # exports (PNG, SVG, CSV, the report): you choose where they go
    def on_download(item):
        target, _ = Q['QFileDialog'].getSaveFileName(win, 'Save', os.path.join(_downloads_dir(), item.downloadFileName()))
        if not target:
            item.cancel()
            return
        item.setDownloadDirectory(os.path.dirname(target))
        item.setDownloadFileName(os.path.basename(target))
        item.accept()

    profile.downloadRequested.connect(on_download)

    if should_raise is not None:      # LinBench was started again: bring this window to the front instead
        timer = Q['QTimer'](win)

        def poll():
            if should_raise():
                if win.isMinimized():
                    win.showMaximized()
                win.raise_()
                win.activateWindow()

        timer.timeout.connect(poll)
        timer.start(300)

    view.setUrl(Q['QUrl'](url))
    win.showMaximized()
    code = app.exec()
    try:                     # the page is released before its profile, as Qt WebEngine wants it
        view.setPage(None)
    except Exception:
        pass
    del view, page
    return code


def selftest(url, timeout_ms=60000):
    """Load ``url`` in the window's engine without showing anything and report, as one line of JSON, whether the
    page loaded, its title and whether LinBench's setup screen was drawn."""
    import json

    os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
    Q = _qt()
    app = _app()
    profile = Q['QWebEngineProfile'](app)
    page = Q['QWebEnginePage'](profile, app)
    out = {'loaded': False, 'title': '', 'drawn': False, 'engine': ''}
    try:
        from PySide6 import __version__ as pyside_version
        from PySide6.QtWebEngineCore import qWebEngineChromiumVersion

        out['engine'] = f'Qt WebEngine {pyside_version} (Chromium {qWebEngineChromiumVersion()})'
    except Exception:
        pass

    def done(ok):
        out['loaded'] = bool(ok)
        if not ok:
            app.quit()
            return

        def check(_=None, tries=[0]):
            def got(res):
                try:
                    res = json.loads(res or '{}')
                except (TypeError, ValueError):
                    res = {}
                out['title'], out['drawn'] = res.get('title', ''), bool(res.get('drawn'))
                if out['drawn'] or tries[0] > 40:
                    app.quit()
                else:
                    tries[0] += 1
                    Q['QTimer'].singleShot(250, check)

            page.runJavaScript("JSON.stringify({title: document.title, drawn: !!(document.querySelector('#setup') && "
                               "!document.querySelector('#setup').hidden && document.querySelector('#btnRun'))})", 0, got)

        check()

    page.loadFinished.connect(done)
    Q['QTimer'].singleShot(timeout_ms, app.quit)
    page.setUrl(Q['QUrl'](url))
    app.exec()
    print(json.dumps(out), flush=True)
    return 0 if out['loaded'] and out['drawn'] else 1
