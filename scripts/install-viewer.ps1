param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SamsungSwitchWatch\Viewer",
    [switch]$StartWithWindows,
    [switch]$DisableStartWithWindows,
    [switch]$DoNotStart,
    [switch]$Preflight
)

. (Join-Path $PSScriptRoot 'common.ps1')

$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Viewer.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk'

Write-SswStep 'Viewer 설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Viewer는 Windows x64에서만 설치할 수 있습니다.' }
if ($StartWithWindows -and $DisableStartWithWindows) {
    throw '-StartWithWindows와 -DisableStartWithWindows는 동시에 사용할 수 없습니다.'
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Viewer 배포 파일을 찾지 못했습니다: $sourceExe" }
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) { throw "패키지 빌드 매니페스트를 찾지 못했습니다: $sourceManifestPath" }
try { $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "패키지 빌드 매니페스트를 읽지 못했습니다: $($_.Exception.Message)" }
if ($sourceManifest.packageKind -ne 'Viewer' -or $sourceManifest.executable.name -ne 'SamsungSwitchWatch.Viewer.exe') {
    throw 'Viewer 패키지 매니페스트 형식이 올바르지 않습니다.'
}
$sourceExeHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceExeHash -ne ([string]$sourceManifest.executable.sha256).ToLowerInvariant()) {
    throw 'Viewer 실행 파일이 빌드 매니페스트의 SHA-256과 일치하지 않습니다.'
}
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
$shortcutBackup = Join-Path ([IO.Path]::GetTempPath()) "SamsungSwitchWatch-Viewer-$transactionId"
$journalPath = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch-Operations\viewer-install.json'
$installSwapped = $false
$shortcutBackupsReady = $false
$shortcutMutationStarted = $false
$transactionCommitted = $false
$smokeProcess = $null
$startMenuExisted = Test-Path -LiteralPath $startMenu -PathType Leaf
$startupExisted = Test-Path -LiteralPath $startup -PathType Leaf

Write-SswOperationJournal -Path $journalPath -Operation 'viewer-install' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

try {
    Write-SswStep '검증된 임시 폴더에 Viewer 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $staging, $shortcutBackup -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $staging -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'SamsungSwitchWatch.Viewer.exe') -PathType Leaf)) {
        throw '임시 폴더의 Viewer 실행 파일 검증에 실패했습니다.'
    }
    if ($startMenuExisted) { Copy-Item -LiteralPath $startMenu -Destination (Join-Path $shortcutBackup 'start-menu.lnk') -Force }
    if ($startupExisted) { Copy-Item -LiteralPath $startup -Destination (Join-Path $shortcutBackup 'startup.lnk') -Force }
    $shortcutBackupsReady = $true

    $viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
    if ($viewerProcesses.Count -gt 0) {
        Write-SswStep '실행 중인 Viewer 종료'
        $viewerProcesses | Stop-Process
        foreach ($process in $viewerProcesses) { try { $process.WaitForExit(5000) | Out-Null } catch { } }
        if (Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue) {
            throw 'Viewer가 종료되지 않았습니다. 창을 닫은 뒤 다시 실행하세요.'
        }
    }

    Write-SswStep 'Viewer 프로그램 폴더 원자적 교체'
    if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    $viewerExe = Join-Path $install 'SamsungSwitchWatch.Viewer.exe'
    $shortcutMutationStarted = $true
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenu)
    $shortcut.TargetPath = $viewerExe
    $shortcut.WorkingDirectory = $install
    $shortcut.Save()
    if ($StartWithWindows) { Copy-Item -LiteralPath $startMenu -Destination $startup -Force }
    elseif ($DisableStartWithWindows -and (Test-Path -LiteralPath $startup -PathType Leaf)) {
        Remove-Item -LiteralPath $startup -Force
    }

    Write-SswStep '새 Viewer 프로세스 자체 점검'
    $smokeProcess = Start-Process -FilePath $viewerExe -WorkingDirectory $install -PassThru
    Start-Sleep -Seconds 5
    $smokeProcess.Refresh()
    if ($smokeProcess.HasExited) { throw "Viewer가 자체 점검 구간에서 종료되었습니다. 종료 코드: $($smokeProcess.ExitCode)" }
    if ($DoNotStart) {
        $smokeProcess.CloseMainWindow() | Out-Null
        if (-not $smokeProcess.WaitForExit(5000)) { $smokeProcess.Kill(); $smokeProcess.WaitForExit(5000) | Out-Null }
    }

    # 성공 상태를 먼저 영구 기록한 뒤 백업을 정리합니다. 이후 정리 실패는 새 설치를 롤백하지 않습니다.
    Write-SswOperationJournal -Path $journalPath -Operation 'viewer-install' -TransactionId $transactionId `
        -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    $transactionCommitted = $true
    $cleanupErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'cleanup-program-backup'; Action = {
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'cleanup-shortcut-backup'; Action = {
            if (Test-Path -LiteralPath $shortcutBackup) { Remove-Item -LiteralPath $shortcutBackup -Recurse -Force }
        } }
    ))
    if ($cleanupErrors.Count -gt 0) {
        Write-Warning ("Viewer 설치는 완료됐지만 이전 버전 백업 정리에 실패했습니다: {0}" -f
            ($cleanupErrors -join ', '))
    }
    Write-SswStep "Viewer 설치 완료: $viewerExe"
}
catch {
    $failure = $_
    if ($transactionCommitted) {
        Write-Warning "Viewer 설치는 완료됐지만 후속 정리에 실패했습니다: $($failure.Exception.Message)"
        return
    }
    Write-Warning 'Viewer 설치 실패를 감지해 이전 버전과 바로 가기를 복구합니다.'
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'stop-new-viewer'; Action = {
            if ($smokeProcess -and -not $smokeProcess.HasExited) { $smokeProcess.Kill(); $smokeProcess.WaitForExit(5000) | Out-Null }
        } },
        [pscustomobject]@{ Name = 'restore-program'; Action = {
            if ($installSwapped -and (Test-Path -LiteralPath $install)) { Remove-Item -LiteralPath $install -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $install }
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'restore-shortcuts'; Action = {
            if (-not $shortcutMutationStarted) { return }
            if (-not $shortcutBackupsReady) { throw '기존 Viewer 바로 가기 백업이 완료되지 않았습니다.' }
            $requiredShortcutBackups = @()
            if ($startMenuExisted) { $requiredShortcutBackups += (Join-Path $shortcutBackup 'start-menu.lnk') }
            if ($startupExisted) { $requiredShortcutBackups += (Join-Path $shortcutBackup 'startup.lnk') }
            $missingShortcutBackups = @($requiredShortcutBackups | Where-Object {
                -not (Test-Path -LiteralPath $_ -PathType Leaf)
            })
            if ($missingShortcutBackups.Count -gt 0) {
                throw '기존 Viewer 바로 가기 백업 파일을 확인하지 못했습니다.'
            }
            foreach ($link in @($startMenu, $startup)) { if (Test-Path -LiteralPath $link -PathType Leaf) { Remove-Item -LiteralPath $link -Force } }
            if ($startMenuExisted) { Copy-Item -LiteralPath (Join-Path $shortcutBackup 'start-menu.lnk') -Destination $startMenu -Force }
            if ($startupExisted) { Copy-Item -LiteralPath (Join-Path $shortcutBackup 'startup.lnk') -Destination $startup -Force }
        } },
        [pscustomobject]@{ Name = 'cleanup-shortcut-backup'; Action = {
            if (Test-Path -LiteralPath $shortcutBackup) { Remove-Item -LiteralPath $shortcutBackup -Recurse -Force }
        } }
    ))
    Write-SswOperationJournal -Path $journalPath -Operation 'viewer-install' -TransactionId $transactionId `
        -Stage 'rollback-completed' -Status 'failed' -Version ([string]$sourceManifest.version) -ErrorCodes $rollbackErrors
    if ($rollbackErrors.Count -gt 0) { Write-Warning ("일부 자동 복구 단계가 실패했습니다: {0}" -f ($rollbackErrors -join ', ')) }
    throw $failure
}
