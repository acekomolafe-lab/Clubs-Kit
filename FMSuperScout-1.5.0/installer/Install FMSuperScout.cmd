@echo off
rem ===========================================================================
rem FMSuperScout Installer Bootstrap
rem Description: Launches install.ps1 with execution policy bypass.
rem ===========================================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
set "EXITCODE=%ERRORLEVEL%"
echo.
pause
exit /b %EXITCODE%
