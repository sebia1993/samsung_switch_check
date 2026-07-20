param(
    [string]$SourceDirectory = (Split-Path $PSScriptRoot -Parent),
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Viewer",
    [switch]$StartWithWindows
)

. (Join-Path $PSScriptRoot 'common.ps1')

$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Viewer.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Viewer 배포 파일을 찾지 못했습니다: $sourceExe" }
if (-not $install.StartsWith(([IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw '기본 안전 범위 밖의 Viewer 설치 폴더는 자동 설치하지 않습니다.'
}
New-Item -ItemType Directory -Path $install -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $install -Recurse -Force

$viewerExe = Join-Path $install 'SamsungSwitchWatch.Viewer.exe'
$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = $viewerExe
$shortcut.WorkingDirectory = $install
$shortcut.Save()
if ($StartWithWindows) {
    $startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk'
    Copy-Item -LiteralPath $startMenu -Destination $startup -Force
}
Write-SswStep "Viewer 설치 완료: $viewerExe"
Start-Process -FilePath $viewerExe
