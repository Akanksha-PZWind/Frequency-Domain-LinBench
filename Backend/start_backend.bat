@echo off
setlocal
cd /d "%~dp0"

echo ==================================================
echo       LinBench Python Backend Engine
echo ==================================================
echo.

REM 1. Check if Python is installed
python --version >nul 2>&1
if %errorlevel% neq 0 (
    py --version >nul 2>&1
    if %errorlevel% neq 0 (
        echo [ERROR] Python is not found in PATH!
        echo Please install Python 3.9 - 3.12 and make sure to check "Add Python to PATH".
        echo Download: https://www.python.org/downloads/
        echo.
        pause
        exit /b 1
    )
    set PY_CMD=py
) else (
    set PY_CMD=python
)

echo Found Python:
%PY_CMD% --version

REM 2. Check and install dependencies
echo Checking dependencies (numpy, scipy)...
%PY_CMD% -c "import numpy, scipy" >nul 2>&1
if %errorlevel% neq 0 (
    echo Installing runtime dependencies: numpy scipy ...
    %PY_CMD% -m pip install -r requirements.txt
    if %errorlevel% neq 0 (
        echo [WARNING] Failed to install dependencies via pip.
        echo Please ensure pip is installed and network access is available.
    )
) else (
    echo Dependencies are already satisfied!
)

REM 3. Start the LinBench engine server
echo.
echo Starting LinBench Python backend on port 8765...
echo (Keep this window open or let the WPF application manage it automatically)
echo.
%PY_CMD% -m linbench --port 8765 --no-browser

pause
