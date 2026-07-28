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
"%SSW_POWERSHELL_PATH%" -NoLogo -NoProfile -Command ^
  "$s = '$Host.UI.RawUI.WindowTitle = ''Samsung Switch Watch - Configure allowed IPs''; try { & $env:SSW_INSTALLER_PATH -ReconfigureAddresses } catch { Write-Host ''''; Write-Host ''Agent allowed IP configuration failed.'' -ForegroundColor Red; Write-Host (''Cause: '' + $_.Exception.Message) -ForegroundColor Yellow; Write-Host ''No successful address change was recorded. Verify the Agent service before reconnecting Viewer.''; [void](Read-Host ''Press Enter after recording the cause above''); exit 1 }'; $e = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($s)); try { $p = Start-Process -FilePath $env:SSW_POWERSHELL_PATH -Verb RunAs -Wait -PassThru -ArgumentList ('-NoLogo -NoProfile -EncodedCommand ' + $e) } catch { Write-Host 'Administrator permission was not granted or elevated PowerShell could not start.' -ForegroundColor Red; exit 5 }; exit $p.ExitCode"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo Agent allowed IP configuration failed.
  echo Review the Cause shown in the elevated PowerShell window.
) else (
  echo Agent allowed IP configuration completed.
)
pause
exit /b %RESULT%
