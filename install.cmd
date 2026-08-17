@echo off
rem Create desktop shortcut for DSH Launcher.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
    echo [install] FAILED - see messages above.
    pause
    exit /b 1
)
pause
