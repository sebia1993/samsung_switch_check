@echo off
setlocal
set "INSTALLER=%~dp0install-viewer.ps1"
set "SSW_POWERSHELL_PATH=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%INSTALLER%" (
  echo install-viewer.ps1 was not found next to this launcher.
  pause
  exit /b 2
)
if not exist "%SSW_POWERSHELL_PATH%" (
  echo Windows PowerShell was not found in System32.
  pause
  exit /b 3
)

echo Installing or updating Viewer for the current Windows user...
"%SSW_POWERSHELL_PATH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -StartWithWindows
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
