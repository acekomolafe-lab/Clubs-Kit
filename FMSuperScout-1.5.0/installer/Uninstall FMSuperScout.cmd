@echo off
rem ===========================================================================
rem FMSuperScout Uninstaller Bootstrap
rem Description: Launches uninstall.ps1 with execution policy bypass.
rem ===========================================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
set "EXITCODE=%ERRORLEVEL%"
pause
exit /b %EXITCODE%
