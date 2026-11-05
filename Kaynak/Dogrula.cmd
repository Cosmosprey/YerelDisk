@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Dogrula.ps1" %*
set "validationResult=%ERRORLEVEL%"
echo.
pause
exit /b %validationResult%
