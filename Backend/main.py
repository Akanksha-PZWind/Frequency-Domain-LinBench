"""Entry point of LinBench.exe (PyInstaller). The exit code is passed on, so --window-test reports pass/fail."""
import sys

from linbench.__main__ import main

if __name__ == '__main__':
    sys.exit(main())
