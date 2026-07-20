param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [switch]$RemoveData
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
if ($RemoveData) {
    Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "서비스 제거 요청에 실패했습니다: $serviceName" }
    Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
}
Get-NetFirewallRule -DisplayName 'Samsung Switch Watch Agent HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if (Test-Path -LiteralPath $install) {
    Write-SswStep "프로그램 폴더 제거: $install"
    Remove-Item -LiteralPath $install -Recurse -Force
}
if ($RemoveData -and (Test-Path -LiteralPath $data)) {
    Write-SswStep "이력·자격 증명 데이터 제거: $data"
    Remove-Item -LiteralPath $data -Recurse -Force
    Write-Warning '데이터 폴더는 복구되지 않습니다.'
}
elseif (Test-Path -LiteralPath $data) {
    Write-Host "데이터는 보존했습니다: $data"
}
