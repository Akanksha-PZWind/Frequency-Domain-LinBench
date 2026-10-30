======================================================================
                     LinBench (WPF + Python Engine)
======================================================================

LinBench consists of two interconnected parts:
1. LinBench.Wpf       : The modern WPF Desktop GUI (C# .NET 8, MVVM)
2. Python Backend     : The calculation engine (linbench, Coleman MBC,
                        modal eigensolvers, ROSCO closed loop, transfer
                        functions, gain study)

BOTH PARTS ARE INCLUDED IN THIS PACKAGE!

DIRECTORY STRUCTURE:
--------------------
- LinBench.sln                 : Visual Studio solution file
- LinBench.Wpf/                : WPF UI project
- Backend/                     : Complete Python backend engine
    - linbench/                : Python source code packages
    - requirements.txt         : Dependencies (numpy, scipy, pyyaml)
    - start_backend.bat        : 1-click script to install dependencies & start
    - main.py                  : Python entry point
    - samples/                 : Linearisation samples (ED_NREL_5MW, etc.)
    - docs/                    : Physics & Architecture documentation
- LinBench/source/             : Mirrored source directory for backward compatibility
- start_backend.bat            : Launcher batch file at root

HOW TO RUN:
-----------
One-time: install Python 3.10 - 3.12 (tick "Add python.exe to PATH").
          The app installs numpy / scipy / pyyaml itself if they are missing
          (same as start_backend.bat).

Option A - Visual Studio 2022:
1. Open LinBench.sln, press F5 (Debug) or Ctrl+F5.
2. LinBench.Wpf starts the Python backend by itself:
       python -m linbench --port 8765 --no-browser
   No .bat file is needed. The dot in the status bar turns green.

Option B - Visual Studio Code (C# Dev Kit extension):
1. File > Open Folder... and pick THIS folder (the one with LinBench.sln).
2. Press F5 and choose "LinBench.Wpf (starts the Python backend)".
   (.vscode\launch.json and tasks.json are included.)
   Or in the terminal:  dotnet run --project LinBench.Wpf

Option C - manual backend (debugging the Python side):
1. Double-click start_backend.bat, keep the window open.
2. Start LinBench.Wpf - it connects to the running backend.

If Python was installed AFTER Visual Studio / VS Code was opened, that is
fine now: the app reads the current PATH from Windows, like the .bat does.

TROUBLESHOOTING:
----------------
- Everything the app did to find/start Python, and every line the backend
  printed, is in:   %USERPROFILE%\.linbench\wpf_backend.log
- Several Pythons installed? Put the full path of the right python.exe in
  Settings > "Custom Python Path", or create Backend\.venv
  (python -m venv .venv  then  .venv\Scripts\pip install numpy scipy pyyaml).
- Port 8765 busy? The app picks a free port by itself.
- Then open Backend\samples\synthetic_rotor to check: 8 operating points
  should appear and "Run analysis" becomes active.
