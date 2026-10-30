"""Checks that a built LinBench folder runs on a PC where nothing was installed (same check as TuneMe's).

    .venv\\Scripts\\python tools\\audit_bundle.py dist\\LinBench

Every .exe, .dll and .pyd in the folder is opened and the libraries it imports are read. Each of them must be
inside the folder or be part of Windows itself (the names below). Anything else - a Visual C++, Fortran, OpenMP or
Python runtime, for instance - would have to be installed on the other PC first, so the build fails here (exit 1).
"""
import os
import sys

import pefile

# Libraries of Windows 10 and 11 themselves. "api-ms-win-*" and "ext-ms-*" are Windows' own API sets.
WINDOWS = {
    'advapi32', 'bcrypt', 'bcryptprimitives', 'cfgmgr32', 'comctl32', 'comdlg32', 'crypt32', 'cryptbase', 'dbghelp',
    'dnsapi', 'dwmapi', 'gdi32', 'gdiplus', 'imm32', 'iphlpapi', 'kernel32', 'kernelbase', 'mpr', 'mswsock', 'msvcrt',
    'ncrypt', 'netapi32', 'normaliz', 'ntdll', 'ole32', 'oleaut32', 'pdh', 'powrprof', 'propsys', 'psapi', 'rpcrt4',
    'secur32', 'setupapi', 'shcore', 'shell32', 'shlwapi', 'user32', 'userenv', 'uxtheme', 'version', 'webservices',
    'winhttp', 'wininet', 'winmm', 'winspool.drv', 'wintrust', 'wldap32', 'ws2_32', 'wsock32', 'wtsapi32',
    # used by the window engine (Qt WebEngine): graphics (Direct3D, DirectWrite, DirectComposition), sound devices,
    # colour profiles, accessibility, input devices, Windows' own ICU. All are part of every Windows 10 and 11.
    'authz', 'bthprops.cpl', 'd3d11', 'd3d12', 'd3d9', 'dcomp', 'dhcpcsvc', 'dwrite', 'dxgi', 'fontsub', 'hid',
    'icuuc', 'imagehlp', 'mmdevapi', 'mscms', 'uiautomationcore', 'urlmon', 'winusb',
    # Media Foundation: imported on demand only, when a page plays video or audio, which LinBench's pages never do
    'mf', 'mfplat', 'mfreadwrite',
}


def imports(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY[k] for k in
                                           ('IMAGE_DIRECTORY_ENTRY_IMPORT', 'IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT')])
    names = [e.dll.decode(errors='replace').lower() for k in ('DIRECTORY_ENTRY_IMPORT', 'DIRECTORY_ENTRY_DELAY_IMPORT')
             for e in getattr(pe, k, [])]
    pe.close()
    return names


def is_windows(name):
    return name.startswith(('api-ms-win-', 'ext-ms-')) or (name[:-4] if name.endswith('.dll') else name) in WINDOWS


def audit(root):
    """(number of libraries looked at, {missing library: [who imports it]})."""
    binaries = [os.path.join(d, f) for d, _, files in os.walk(root) for f in files
                if f.lower().endswith(('.exe', '.dll', '.pyd')) and os.sep + 'source' + os.sep not in os.path.join(d, f)]
    have = {os.path.basename(b).lower() for b in binaries}
    missing = {}
    for b in binaries:
        for name in imports(b):
            if name in have or is_windows(name):
                continue
            missing.setdefault(name, []).append(os.path.relpath(b, root))
    return len(binaries), missing


def main(root):
    n, missing = audit(root)
    if missing:
        print(f'NOT SELF-CONTAINED: {len(missing)} libraries are imported that are neither in {root} nor part of Windows:')
        for name, users in sorted(missing.items()):
            print(f"  {name}  <- {', '.join(users[:3])}{' ...' if len(users) > 3 else ''}")
        return 1
    print(f'Self-contained: {n} libraries import nothing but each other and Windows itself.')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))
