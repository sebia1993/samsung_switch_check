@echo off
setlocal
set "INSTALLER=%~dp0install-viewer.ps1"
if not exist "%INSTALLER%" (
  echo install-viewer.ps1 was not found next to this launcher.
  pause
  exit /b 2
)

echo Installing or updating Viewer for the current Windows user...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -StartWithWindows
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo Viewer install or update failed.
  echo Review the error shown above. See INSTALL_KO.md.
) else (
  echo Viewer installation or update completed.
  echo Viewer will start automatically when this Windows user signs in.
)
pause
exit /b %RESULT%
