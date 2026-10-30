"""Writes tools/version_info.txt (the version shown in the Properties of LinBench.exe) from linbench.__version__.
    python tools/make_version_info.py 1.1.0"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

TEMPLATE = """VSVersionInfo(
  ffi=FixedFileInfo(
    filevers=({t}),
    prodvers=({t}),
    mask=0x3f,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
  ),
  kids=[
    StringFileInfo([
      StringTable('040904B0', [
        StringStruct('CompanyName', 'LinBench'),
        StringStruct('FileDescription', 'LinBench - open- and closed-loop analysis of OpenFAST linearisations'),
        StringStruct('FileVersion', '{v}'),
        StringStruct('InternalName', 'LinBench'),
        StringStruct('OriginalFilename', 'LinBench.exe'),
        StringStruct('ProductName', 'LinBench'),
        StringStruct('ProductVersion', '{v}'),
        StringStruct('Comments', 'Self-contained: Python, numpy, scipy and Qt WebEngine travel inside the folder')
      ])
    ]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"""


def main(v):
    parts = [int(p) for p in v.split('.')[:3]] + [0] * 4
    t = ', '.join(str(p) for p in parts[:4])
    with open(os.path.join(HERE, 'version_info.txt'), 'w', encoding='utf-8') as fh:
        fh.write(TEMPLATE.format(t=t, v=v))
    print('version info', v)


if __name__ == '__main__':
    sys.path.insert(0, os.path.dirname(HERE))
    from linbench import __version__
    main(sys.argv[1] if len(sys.argv) > 1 else __version__)
