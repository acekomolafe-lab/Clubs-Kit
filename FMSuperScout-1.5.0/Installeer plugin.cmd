@echo off
rem ===========================================================================
rem FMSuperScout Plugin Installer
rem Description: Copies FMSuperScout.dll to the BepInEx plugins folder for FM26.
rem ===========================================================================
title FMSuperScout - plugin installeren
setlocal

set "SRC=%~dp0plugin\dist\FMSuperScout.dll"
set "DST=E:\SteamLibrary\steamapps\common\Football Manager 26\BepInEx\plugins"

echo FMSuperScout plugin installeren
echo.

if exist "%SRC%" goto srcok
echo [FOUT] Bronbestand niet gevonden:
echo        %SRC%
echo        Bouw de plugin eerst (dotnet build plugin -c Release en kopieer naar plugin\dist).
timeout /t 5
exit /b 1
:srcok
for %%F in ("%SRC%") do echo Te installeren build: %%~tF - %%~zF bytes

if exist "%DST%" goto dstok
echo [FOUT] BepInEx plugins-map niet gevonden op:
echo        %DST%
echo        Klopt je Steam-installatiepad?
timeout /t 5
exit /b 1
:dstok
if not exist "%DST%\FMSuperScout.dll" goto check
for %%F in ("%DST%\FMSuperScout.dll") do echo Nu geinstalleerd:      %%~tF - %%~zF bytes

:check
tasklist /FI "IMAGENAME eq fm.exe" 2>NUL | find /I "fm.exe" >NUL
if errorlevel 1 goto copy
echo.
echo [let op] Football Manager 26 draait nog; de plugin-DLL is dan vergrendeld.
echo          Sluit de game VOLLEDIG af.
set killfm=
set /p killfm="Wil je dat ik het spel (of een achtergebleven zombie-proces) forceer afsluit? (y/n): "
if /I "%killfm:~0,1%"=="y" goto do_kill
if /I "%killfm:~0,1%"=="j" goto do_kill
echo Druk daarna op een toets om opnieuw te controleren...
timeout /t 5 >NUL
goto check

:do_kill
echo Forceer afsluiten fm.exe...
taskkill /F /IM fm.exe >NUL 2>&1
timeout /t 2 >NUL
goto copy

:copy
copy /Y "%SRC%" "%DST%\FMSuperScout.dll" >NUL 2>&1
if not errorlevel 1 goto ok
echo.
echo [FOUT] Kopieren mislukt.
echo        - Draait FM26 toch nog? Controleer Taakbeheer op fm.exe.
echo        - Geen schrijfrechten? Rechtsklik dit script en kies
echo          "Als administrator uitvoeren".
timeout /t 5
exit /b 1

:ok
echo.
for %%F in ("%DST%\FMSuperScout.dll") do echo [OK] Geinstalleerd:    %%~tF - %%~zF bytes
echo      Start FM26; de nieuwe plugin laadt vanzelf mee.
echo.
timeout /t 5
exit /b 0
