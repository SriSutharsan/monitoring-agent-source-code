@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

:: -------------------------------------------------------------
:: Check for Administrator Privileges
:: -------------------------------------------------------------
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo =========================================================
    echo   [ERROR] Administrator privileges required!
    echo   Please right-click this file and choose:
    echo   "Run as administrator"
    echo =========================================================
    echo.
    pause
    exit /b 1
)

cls
echo ====================================================================
echo             Endpoint Monitoring Agent Installer
echo             Server: https://rmm.voyagermarine.online
echo ====================================================================
echo.

set "DEFAULT_TOKEN=default-token-12345"

echo Please enter your Enrollment / Registration Token.
echo (You can find or create tokens in your dashboard under "Enrollment Tokens")
echo.
set "USER_TOKEN="
set /p USER_TOKEN="Enter Token (or press ENTER to use default): "

if "!USER_TOKEN!"=="" (
    set "FINAL_TOKEN=%DEFAULT_TOKEN%"
    echo Using default token: %DEFAULT_TOKEN%
) else (
    set "FINAL_TOKEN=!USER_TOKEN!"
    echo Using token: !FINAL_TOKEN!
)

echo.
echo Installing agent and connecting to https://rmm.voyagermarine.online...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0install.ps1' -ApiUrl 'https://rmm.voyagermarine.online' -RegistrationToken '!FINAL_TOKEN!'"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Installation failed. Check the error message above.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ====================================================================
echo   [SUCCESS] Agent successfully installed and registered!
echo.
echo   Next Step:
echo   1. Open your browser: https://rmm.voyagermarine.online
echo   2. Look under "Pending Enrollment" and click "Approve"
echo ====================================================================
echo.
pause
