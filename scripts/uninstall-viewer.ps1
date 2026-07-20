param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Viewer",
    [switch]$RemoveSettings
)

. (Join-Path $PSScriptRoot 'common.ps1')

$install = [IO.Path]::GetFullPath($InstallDirectory)
if (-not $install.StartsWith(([IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw '기본 안전 범위 밖의 Viewer 설치 폴더는 자동 제거하지 않습니다.'
}
Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue | Stop-Process
$links = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk')
)
foreach ($link in $links) { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
if ($RemoveSettings) {
    $settings = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch'
    if (Test-Path -LiteralPath $settings) {
        Remove-Item -LiteralPath $settings -Recurse -Force
        Write-Warning 'Viewer 토큰과 화면 설정을 제거했으며 복구되지 않습니다.'
    }
}
Write-SswStep 'Viewer 제거 완료'
