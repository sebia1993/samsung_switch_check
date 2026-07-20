param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$ServiceName = 'SamsungSwitchWatchAgent'
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$exe = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'SamsungSwitchWatch.Agent.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Agent 실행 파일을 찾지 못했습니다: $exe" }
$environment = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment).Environment
$certificatePassword = $environment | Where-Object { $_ -like 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=*' } | Select-Object -First 1
$env:DOTNET_ENVIRONMENT = 'Production'
if ($certificatePassword) { $env:SAMSUNG_SWITCH_WATCH_CERT_PASSWORD = $certificatePassword.Substring($certificatePassword.IndexOf('=') + 1) }

Write-SswStep '10분간 유효한 일회용 페어링 코드 생성'
& $exe pairing create
if ($LASTEXITCODE -ne 0) { throw '페어링 코드 생성에 실패했습니다.' }
