param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$ServiceName = 'SamsungSwitchWatchAgent',
    [string]$CredentialId = 'samsung-switch-readonly',
    [Parameter(Mandatory = $true)][string]$Username
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$exe = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'SamsungSwitchWatch.Agent.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Agent 실행 파일을 찾지 못했습니다: $exe" }

$environment = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment).Environment
$certificatePassword = $environment | Where-Object { $_ -like 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=*' } | Select-Object -First 1
$env:DOTNET_ENVIRONMENT = 'Production'
if ($certificatePassword) { $env:SAMSUNG_SWITCH_WATCH_CERT_PASSWORD = $certificatePassword.Substring($certificatePassword.IndexOf('=') + 1) }

Write-SswStep '비밀번호를 로컬 대화형 입력으로 DPAPI 저장'
& $exe credential set $CredentialId $Username
if ($LASTEXITCODE -ne 0) { throw '스위치 자격 증명 저장에 실패했습니다.' }
$service = Get-Service -Name $ServiceName
if ($service.Status -eq 'Running') { Restart-Service -Name $ServiceName }
else { Start-Service -Name $ServiceName }
Write-SswStep '자격 증명 저장 및 Agent 재시작 완료'
