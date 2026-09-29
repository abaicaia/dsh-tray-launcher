@echo off
rem Rebuild DshLauncher.exe and reinstall desktop shortcuts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-launcher.ps1"
if errorlevel 1 (
    echo [build] FAILED - see messages above.
    pause
    exit /b 1
)
