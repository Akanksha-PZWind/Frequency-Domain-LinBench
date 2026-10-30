# PyInstaller build specification for LinBench (same approach as TuneMe)
#   Build:  powershell -ExecutionPolicy Bypass -File build.ps1
#   Result: dist\LinBench\LinBench.exe  - a self-contained folder: Python, numpy/scipy, Qt WebEngine and LinBench
import os

block_cipher = None
ROOT = os.path.abspath(SPECPATH)

datas = [
    (os.path.join(ROOT, 'linbench', 'web'), os.path.join('linbench', 'web')),
    (os.path.join(ROOT, 'assets'), 'assets'),
]

hiddenimports = [
    'scipy.optimize', 'scipy.interpolate', 'scipy.linalg', 'scipy.special', 'yaml', 'tkinter', 'tkinter.filedialog',
    # LinBench's own window (linbench/window.py): Qt WebEngine, a Chromium engine that travels inside the folder
    'PySide6.QtCore', 'PySide6.QtGui', 'PySide6.QtWidgets', 'PySide6.QtNetwork', 'PySide6.QtWebChannel',
    'PySide6.QtWebEngineCore', 'PySide6.QtWebEngineWidgets',
]

excludes = [
    'matplotlib', 'pandas', 'IPython', 'notebook', 'pytest', 'PyQt5', 'PyQt6', 'PySide2', 'tornado', 'sqlalchemy',
    # parts of Qt the window does not use
    'PySide6.QtQuick3D', 'PySide6.QtCharts', 'PySide6.QtDataVisualization', 'PySide6.QtGraphs', 'PySide6.Qt3DCore',
    'PySide6.Qt3DRender', 'PySide6.QtMultimedia', 'PySide6.QtMultimediaWidgets', 'PySide6.QtSql', 'PySide6.QtTest',
    'PySide6.QtBluetooth', 'PySide6.QtNfc', 'PySide6.QtSerialPort', 'PySide6.QtSensors', 'PySide6.QtDesigner',
    'PySide6.QtHelp', 'PySide6.QtPdf', 'PySide6.QtPdfWidgets', 'PySide6.QtSpatialAudio', 'PySide6.QtTextToSpeech',
]

a = Analysis(
    [os.path.join(ROOT, 'main.py')],
    pathex=[ROOT],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=excludes,
    noarchive=False,
    cipher=block_cipher,
)


# Of Qt only what the window loads goes into the folder (as in TuneMe): the Qt libraries below (QWebEngineView and
# what it imports), no QML modules, no debug resource packs, no Qt translations (LinBench's screens are its own) and
# of the engine's language packs English only. tools/audit_bundle.py checks afterwards that nothing left in the
# folder needs a part that was dropped.
_QT_WINDOW = {
    'qt6core', 'qt6gui', 'qt6widgets', 'qt6network', 'qt6opengl', 'qt6svg', 'qt6printsupport', 'qt6positioning',
    'qt6qml', 'qt6qmlmeta', 'qt6qmlmodels', 'qt6qmlworkerscript', 'qt6quick', 'qt6quickwidgets', 'qt6virtualkeyboard',
    'qt6webchannel', 'qt6webenginecore', 'qt6webenginewidgets',
}


def _keep(dest):
    d = dest.replace('\\', '/').lower()
    if not d.startswith('pyside6/'):
        return True
    rel, name = d[len('pyside6/'):], d.rsplit('/', 1)[-1]
    if rel.startswith(('qml/', 'plugins/qmltooling/', 'plugins/position/')) or name == 'qdirect2d.dll' or '.debug.' in name:
        return False
    if '/' not in rel and name.startswith('qt6') and name.endswith('.dll'):
        return name[:-4] in _QT_WINDOW
    if rel.startswith('translations/qtwebengine_locales/'):
        return name in ('en-us.pak', 'en-gb.pak')
    return not (rel.startswith('translations/') and name.endswith('.qm'))


a.binaries = [x for x in a.binaries if _keep(x[0])]
a.datas = [x for x in a.datas if _keep(x[0])]
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='LinBench',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    icon=os.path.join(ROOT, 'assets', 'linbench.ico'),
    version=os.path.join(ROOT, 'tools', 'version_info.txt'),
)
coll = COLLECT(exe, a.binaries, a.zipfiles, a.datas, strip=False, upx=False, name='LinBench')
