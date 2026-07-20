param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [string]$ServiceName = 'SamsungSwitchWatchAgent',
    [switch]$RemoveData
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
if (-not $install.StartsWith(([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw '기본 안전 범위 밖의 설치 폴더는 자동 제거하지 않습니다.'
}
if ($RemoveData -and -not $data.StartsWith(([IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw '기본 안전 범위 밖의 데이터 폴더는 자동 제거하지 않습니다.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
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
