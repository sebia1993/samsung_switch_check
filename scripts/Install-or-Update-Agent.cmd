@echo off
setlocal
set "INSTALLER=%~dp0install-agent.ps1"
if not exist "%INSTALLER%" (
  echo install-agent.ps1 was not found next to this launcher.
  pause
  exit /b 2
)

echo Requesting administrator permission (UAC)...
set "SSW_INSTALLER_PATH=%INSTALLER%"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = 'try { & $env:SSW_INSTALLER_PATH } catch { Write-Error $_; [void](Read-Host ''Press Enter to close this error window''); exit 1 }'; $e = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($s)); $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList ('-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $e); exit $p.ExitCode"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo Agent install or update failed.
  echo Review the error shown in the elevated PowerShell window. See INSTALL_KO.md.
) else (
  echo Agent installation or update completed.
  echo See INSTALL_KO.md for Viewer connection and device registration.
)
pause
exit /b %RESULT%
