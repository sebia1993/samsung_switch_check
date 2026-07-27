@echo off
setlocal
set "INSTALLER=%~dp0install-agent.ps1"
set "SSW_POWERSHELL_PATH=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%INSTALLER%" (
  echo install-agent.ps1 was not found next to this launcher.
  pause
  exit /b 2
)
if not exist "%SSW_POWERSHELL_PATH%" (
  echo Windows PowerShell was not found in System32.
  pause
  exit /b 3
)

echo Requesting administrator permission (UAC)...
set "SSW_INSTALLER_PATH=%INSTALLER%"
"%SSW_POWERSHELL_PATH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = '$Host.UI.RawUI.WindowTitle = ''Samsung Switch Watch - Agent installer''; try { & $env:SSW_INSTALLER_PATH } catch { Write-Host ''''; Write-Host ''Agent installation failed.'' -ForegroundColor Red; Write-Host (''Cause: '' + $_.Exception.Message) -ForegroundColor Yellow; Write-Host ''The requested Agent installation did not complete. Verify the service before reconnecting Viewer.''; [void](Read-Host ''Press Enter after recording the cause above''); exit 1 }'; $e = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($s)); try { $p = Start-Process -FilePath $env:SSW_POWERSHELL_PATH -Verb RunAs -Wait -PassThru -ArgumentList ('-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $e) } catch { Write-Host 'Administrator permission was not granted or elevated PowerShell could not start.' -ForegroundColor Red; exit 5 }; exit $p.ExitCode"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo Agent install or update failed.
  echo The new Agent installation did not complete.
  echo If no Agent is listening on TCP/18443, Viewer reports AGENT_CONNECTION_REFUSED.
  echo Review the Cause shown in the elevated PowerShell window. See INSTALL_KO.md.
) else (
  echo Agent installation or update completed.
  echo See INSTALL_KO.md for Viewer connection and device registration.
)
pause
exit /b %RESULT%
