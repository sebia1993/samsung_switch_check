param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent"
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$exe = Join-Path $install 'SamsungSwitchWatch.Agent.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Agent 실행 파일을 찾지 못했습니다: $exe" }
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { throw "Agent 서비스를 찾지 못했습니다: $serviceName" }
$environment = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment).Environment
$certificatePassword = $environment | Where-Object { $_ -like 'SAMSUNG_SWITCH_WATCH_CERT_PASSWORD=*' } | Select-Object -First 1
$oldDotnetEnvironment = $env:DOTNET_ENVIRONMENT
$oldCertificatePassword = $env:SAMSUNG_SWITCH_WATCH_CERT_PASSWORD
$env:DOTNET_ENVIRONMENT = 'Production'
if ($certificatePassword) { $env:SAMSUNG_SWITCH_WATCH_CERT_PASSWORD = $certificatePassword.Substring($certificatePassword.IndexOf('=') + 1) }

try {
    Push-Location -LiteralPath $install
    try {
        Write-SswStep '10분간 유효한 일회용 페어링 코드 생성'
        & $exe pairing create
        if ($LASTEXITCODE -ne 0) { throw '페어링 코드 생성에 실패했습니다.' }
    }
    finally { Pop-Location }
}
finally {
    $env:DOTNET_ENVIRONMENT = $oldDotnetEnvironment
    $env:SAMSUNG_SWITCH_WATCH_CERT_PASSWORD = $oldCertificatePassword
    $certificatePassword = $null
}
