param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Viewer",
    [switch]$RemoveSettings
)

. (Join-Path $PSScriptRoot 'common.ps1')

$install = [IO.Path]::GetFullPath($InstallDirectory)
Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer'
$viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
if ($viewerProcesses.Count -gt 0) {
    $viewerProcesses | Stop-Process
    foreach ($process in $viewerProcesses) {
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
    Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue | Stop-Process -Force
}
$links = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk')
)
foreach ($link in $links) { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
if ($RemoveSettings) {
    $settings = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch'
    Assert-SswProductPath -Path $settings -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'SamsungSwitchWatch'
    if (Test-Path -LiteralPath $settings) {
        Remove-Item -LiteralPath $settings -Recurse -Force
        Write-Warning 'Viewer 토큰과 화면 설정을 제거했으며 복구되지 않습니다.'
    }
}
Write-SswStep 'Viewer 제거 완료'
