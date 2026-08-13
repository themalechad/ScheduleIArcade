@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Build-And-Install.ps1" %*
set ERR=%ERRORLEVEL%

echo.
if not "%ERR%"=="0" (
  echo Build failed with exit code %ERR%.
) else (
  echo Build/install finished successfully.
)
pause
exit /b %ERR%
