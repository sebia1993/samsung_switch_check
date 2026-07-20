param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Viewer",
    [switch]$StartWithWindows,
    [switch]$DoNotStart,
    [switch]$Preflight
)

. (Join-Path $PSScriptRoot 'common.ps1')

$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Viewer.exe'

Write-SswStep 'Viewer 설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Viewer는 Windows x64에서만 설치할 수 있습니다.' }
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Viewer 배포 파일을 찾지 못했습니다: $sourceExe" }
Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer'
if ($source.TrimEnd('\') -eq $install.TrimEnd('\')) { throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.' }

Write-Host "  source  : $source"
Write-Host "  install : $install"
if ($Preflight) {
    Write-SswStep '사전 검사를 통과했습니다. 시스템은 변경되지 않았습니다.'
    return
}

$installParent = Split-Path $install -Parent
$transactionId = [Guid]::NewGuid().ToString('N')
$staging = "$install.__staging_$transactionId"
$backup = "$install.__backup_$transactionId"
$installSwapped = $false

try {
    Write-SswStep '검증된 임시 폴더에 Viewer 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $staging -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'SamsungSwitchWatch.Viewer.exe') -PathType Leaf)) {
        throw '임시 폴더의 Viewer 실행 파일 검증에 실패했습니다.'
    }

    $viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
    if ($viewerProcesses.Count -gt 0) {
        Write-SswStep '실행 중인 Viewer 종료'
        $viewerProcesses | Stop-Process
        foreach ($process in $viewerProcesses) {
            try { $process.WaitForExit(5000) | Out-Null } catch { }
        }
        $remaining = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
        if ($remaining.Count -gt 0) { throw 'Viewer가 종료되지 않았습니다. 창을 닫은 뒤 다시 실행하세요.' }
    }

    Write-SswStep 'Viewer 프로그램 폴더 원자적 교체'
    if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    $viewerExe = Join-Path $install 'SamsungSwitchWatch.Viewer.exe'
    $shell = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'
    $shortcut = $shell.CreateShortcut($startMenu)
    $shortcut.TargetPath = $viewerExe
    $shortcut.WorkingDirectory = $install
    $shortcut.Save()
    $startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk'
    if ($StartWithWindows) { Copy-Item -LiteralPath $startMenu -Destination $startup -Force }

    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    Write-SswStep "Viewer 설치 완료: $viewerExe"
    if (-not $DoNotStart) { Start-Process -FilePath $viewerExe -WorkingDirectory $install }
}
catch {
    $failure = $_
    Write-Warning 'Viewer 설치 실패를 감지해 이전 버전을 복구합니다.'
    try {
        if ($installSwapped -and (Test-Path -LiteralPath $install)) { Remove-Item -LiteralPath $install -Recurse -Force }
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $install }
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
    catch {
        Write-Warning "자동 복구 중 추가 오류가 발생했습니다: $($_.Exception.Message)"
    }
    throw $failure
}
