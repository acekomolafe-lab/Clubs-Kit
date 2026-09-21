@echo off
tasklist /FI "IMAGENAME eq something_random.exe" 2>NUL | find /I "fm.exe" >NUL
echo errorlevel is %ERRORLEVEL%
