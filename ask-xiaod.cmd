@echo off
chcp 65001 >nul
setlocal
echo.
echo ask-xiaod: sending the DSH crime scene to XiaoD (headless channel)...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ask-xiaod.ps1" %*
echo.
pause
