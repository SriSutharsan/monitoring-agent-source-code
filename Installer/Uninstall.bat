@echo off
setlocal
echo ===================================================
echo   Uninstalling Windows Endpoint Monitoring Agent
echo ===================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
pause
