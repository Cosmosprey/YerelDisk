@echo off
set "YERELDISK_BUILD_SCRIPT=%~dp0Derle.ps1"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "try { & $env:YERELDISK_BUILD_SCRIPT; exit 0 } catch { Write-Host $_ -ForegroundColor Red; exit 1 }"
set "buildResult=%ERRORLEVEL%"
echo.
pause
exit /b %buildResult%
