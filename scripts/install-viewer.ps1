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
$startMenuParent = Split-Path $startMenu -Parent
$startupParent = Split-Path $startup -Parent

function Get-SswValidatedViewerPackage {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    $resolvedDirectory = [IO.Path]::GetFullPath($Directory)
    $resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
    Assert-SswChildPath -Parent $resolvedDirectory -Child $resolvedManifest
    if (-not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)) {
        throw "VIEWER_PACKAGE_FILE_MISSING: 패키지 빌드 매니페스트를 찾지 못했습니다: $resolvedManifest"
    }
    $manifestItem = Get-Item -LiteralPath $resolvedManifest -Force
    if (($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'BUILD-MANIFEST.json은 junction 또는 symlink일 수 없습니다.'
    }

    try {
        $manifest = Get-Content -LiteralPath $resolvedManifest -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw "패키지 빌드 매니페스트를 읽지 못했습니다: $($_.Exception.Message)"
    }
    if (-not $manifest.PSObject.Properties['manifestVersion'] -or
        -not $manifest.PSObject.Properties['product'] -or
        -not $manifest.PSObject.Properties['packageKind'] -or
        -not $manifest.PSObject.Properties['runtimeIdentifier'] -or
        -not $manifest.PSObject.Properties['version'] -or
        -not $manifest.PSObject.Properties['executable'] -or
        -not $manifest.PSObject.Properties['files'] -or
        $manifest.manifestVersion -ne 1 -or
        $manifest.product -ne 'SamsungSwitchWatch' -or
        $manifest.packageKind -ne 'Viewer' -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or
        [string]::IsNullOrWhiteSpace([string]$manifest.version) -or
        $null -eq $manifest.executable) {
        throw 'Viewer 패키지 매니페스트 형식이 올바르지 않습니다.'
    }
    if (-not $manifest.executable.PSObject.Properties['name'] -or
        -not $manifest.executable.PSObject.Properties['sha256'] -or
        [string]$manifest.executable.name -cne 'SamsungSwitchWatch.Viewer.exe' -or
        [string]$manifest.executable.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Viewer 실행 파일 identity가 패키지 매니페스트와 일치하지 않습니다.'
    }

    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -eq 0) {
        throw 'Viewer 패키지 매니페스트에 파일 목록이 없습니다.'
    }
    $manifestNames = New-Object Collections.Generic.HashSet[string](
        [StringComparer]::OrdinalIgnoreCase)
    $validatedFiles = New-Object Collections.Generic.List[object]
    foreach ($file in $manifestFiles) {
        if ($null -eq $file -or
            -not $file.PSObject.Properties['name'] -or
            -not $file.PSObject.Properties['size'] -or
            -not $file.PSObject.Properties['sha256']) {
            throw 'Viewer 패키지 파일 identity가 올바르지 않습니다.'
        }
        $name = [string]$file.name
        if ([string]::IsNullOrWhiteSpace($name) -or
            [IO.Path]::GetFileName($name) -cne $name -or
            $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $name.TrimEnd([char[]]@(' ', '.')) -cne $name -or
            $name -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$' -or
            $name -ieq 'BUILD-MANIFEST.json' -or
            -not $manifestNames.Add($name)) {
            throw "안전하지 않거나 중복된 패키지 파일 이름입니다: $name"
        }

        $declaredSize = 0L
        $sizeText = [Convert]::ToString(
            $file.size,
            [Globalization.CultureInfo]::InvariantCulture)
        if (-not [long]::TryParse(
                $sizeText,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$declaredSize) -or
            $declaredSize -lt 0) {
            throw "패키지 파일 크기가 올바르지 않습니다: $name"
        }
        $declaredHash = ([string]$file.sha256).ToLowerInvariant()
        if ($declaredHash -notmatch '^[0-9a-f]{64}$') {
            throw "패키지 파일 SHA-256이 올바르지 않습니다: $name"
        }

        $path = Join-Path $resolvedDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "VIEWER_PACKAGE_FILE_MISSING: 패키지 파일을 찾지 못했습니다: $name"
        }
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Name -cne $name -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "패키지 파일 identity가 올바르지 않습니다: $name"
        }
        if ([long]$item.Length -ne $declaredSize) {
            throw "VIEWER_PACKAGE_HASH_MISMATCH: 패키지 파일 크기가 BUILD-MANIFEST.json과 일치하지 않습니다: $name"
        }
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $declaredHash) {
            throw "VIEWER_PACKAGE_HASH_MISMATCH: 패키지 파일 SHA-256이 BUILD-MANIFEST.json과 일치하지 않습니다: $name"
        }
        $validatedFiles.Add([pscustomobject]@{
            Name = $name
            Size = $declaredSize
            Sha256 = $declaredHash
        })
    }

    $sourceItems = @(Get-ChildItem -LiteralPath $resolvedDirectory -Force -ErrorAction Stop)
    $unexpectedDirectories = @($sourceItems | Where-Object { $_.PSIsContainer })
    if ($unexpectedDirectories.Count -gt 0) {
        throw "Viewer 패키지에는 하위 폴더를 포함할 수 없습니다: $($unexpectedDirectories[0].Name)"
    }
    $actualPayloadNames = @($sourceItems |
        Where-Object { -not $_.PSIsContainer -and $_.Name -ine 'BUILD-MANIFEST.json' } |
        ForEach-Object { $_.Name })
    $actualNames = New-Object Collections.Generic.HashSet[string](
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($actualName in $actualPayloadNames) {
        $null = $actualNames.Add([string]$actualName)
    }
    if ($actualNames.Count -ne $manifestNames.Count) {
        throw 'Viewer 패키지의 실제 파일 목록이 BUILD-MANIFEST.json과 일치하지 않습니다.'
    }
    foreach ($actualName in $actualNames) {
        if (-not $manifestNames.Contains($actualName)) {
            throw "BUILD-MANIFEST.json에 없는 패키지 파일이 있습니다: $actualName"
        }
    }

    $executableEntries = @($validatedFiles | Where-Object {
        $_.Name -ceq 'SamsungSwitchWatch.Viewer.exe'
    })
    if ($executableEntries.Count -ne 1 -or
        $executableEntries[0].Sha256 -ne
            ([string]$manifest.executable.sha256).ToLowerInvariant()) {
        throw 'Viewer 실행 파일 identity 또는 SHA-256이 파일 목록과 일치하지 않습니다.'
    }

    $manifestHash = (Get-FileHash -LiteralPath $resolvedManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{
        Manifest = $manifest
        ManifestSha256 = $manifestHash
        Files = $validatedFiles.ToArray()
    }
}

Write-SswStep 'Viewer 설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Viewer는 Windows x64에서만 설치할 수 있습니다.' }
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'VIEWER_UNSUPPORTED_ARCHITECTURE: Viewer는 64비트 Windows에서만 설치할 수 있습니다.'
}
if ($StartWithWindows -and $DisableStartWithWindows) {
    throw '-StartWithWindows와 -DisableStartWithWindows는 동시에 사용할 수 없습니다.'
}
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "VIEWER_PACKAGE_FILE_MISSING: Viewer 배포 파일을 찾지 못했습니다: $sourceExe"
}
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
    throw "VIEWER_PACKAGE_FILE_MISSING: 패키지 빌드 매니페스트를 찾지 못했습니다: $sourceManifestPath"
}
$sourcePackage = Get-SswValidatedViewerPackage -Directory $source -ManifestPath $sourceManifestPath
$sourceManifest = $sourcePackage.Manifest
Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer'
if ($source.TrimEnd('\') -eq $install.TrimEnd('\')) { throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.' }

Write-Host "  source  : $source"
Write-Host "  install : $install"
if ($Preflight) {
    Write-SswStep '사전 검사를 통과했습니다. 시스템은 변경되지 않았습니다.'
    return
}

$deploymentLock = Enter-SswDeploymentLock -Product 'Viewer'
try {
$installParent = Split-Path $install -Parent
$transactionId = [Guid]::NewGuid().ToString('N')
$staging = "$install.__staging_$transactionId"
$backup = "$install.__backup_$transactionId"
$shortcutBackup = Join-Path ([IO.Path]::GetTempPath()) "SamsungSwitchWatch-Viewer-$transactionId"
$journalPath = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch-Operations\viewer-install.json'
$installSwapped = $false
$shortcutBackupsReady = $false
$shortcutMutationStarted = $false
$rollbackState = [pscustomobject]@{ ShortcutRestored = $false }
$startMenuParentCreated = $false
$startupParentCreated = $false
$transactionCommitted = $false
$failureCode = 'VIEWER_PACKAGE_PREPARE_FAILED'
$failureDetailCode = $null
$failureExitCode = $null
$smokeProcess = $null
$viewerRuntimeDiagnosticPath = Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch\logs\viewer-diagnostic.jsonl'
$previousInstallExisted = Test-Path -LiteralPath $install -PathType Container
$startMenuExisted = Test-Path -LiteralPath $startMenu -PathType Leaf
$startupExisted = Test-Path -LiteralPath $startup -PathType Leaf

Write-SswOperationJournal -Path $journalPath -Operation 'viewer-install' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running' -Version ([string]$sourceManifest.version)

try {
    Write-SswStep '검증된 임시 폴더에 Viewer 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $staging, $shortcutBackup -Force | Out-Null
    foreach ($file in @($sourcePackage.Files)) {
        Copy-Item -LiteralPath (Join-Path $source ([string]$file.Name)) -Destination $staging -Force
    }
    Copy-Item -LiteralPath $sourceManifestPath -Destination $staging -Force
    Write-SswStep '임시 폴더의 전체 Viewer 패키지 재검증'
    $stagedManifestPath = Join-Path $staging 'BUILD-MANIFEST.json'
    $stagedPackage = Get-SswValidatedViewerPackage -Directory $staging -ManifestPath $stagedManifestPath
    if ($stagedPackage.ManifestSha256 -ne $sourcePackage.ManifestSha256) {
        throw '임시 폴더의 BUILD-MANIFEST.json이 검증한 원본과 일치하지 않습니다.'
    }
    if ($startMenuExisted) { Copy-Item -LiteralPath $startMenu -Destination (Join-Path $shortcutBackup 'start-menu.lnk') -Force }
    if ($startupExisted) { Copy-Item -LiteralPath $startup -Destination (Join-Path $shortcutBackup 'startup.lnk') -Force }
    $shortcutBackupsReady = $true

    $failureCode = 'VIEWER_PROCESS_STOP_FAILED'
    $viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
    if ($viewerProcesses.Count -gt 0) {
        Write-SswStep '실행 중인 Viewer 종료'
        $viewerProcesses | Stop-Process
        foreach ($process in $viewerProcesses) { try { $process.WaitForExit(5000) | Out-Null } catch { } }
        if (Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue) {
            throw 'Viewer가 종료되지 않았습니다. 창을 닫은 뒤 다시 실행하세요.'
        }
    }

    $failureCode = 'VIEWER_PROGRAM_SWAP_FAILED'
    Write-SswStep 'Viewer 프로그램 폴더 원자적 교체'
    if (Test-Path -LiteralPath $install) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    $failureCode = 'VIEWER_SHORTCUT_SETUP_FAILED'
    Write-SswStep 'Viewer 바로 가기 폴더 준비'
    $startMenuParentCreated = New-SswDirectoryIfMissing -Path $startMenuParent `
        -FailureCode 'VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE' -Description '시작 메뉴'
    if ($StartWithWindows) {
        $startupParentCreated = New-SswDirectoryIfMissing -Path $startupParent `
            -FailureCode 'VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE' -Description '시작프로그램'
    }

    $viewerExe = Join-Path $install 'SamsungSwitchWatch.Viewer.exe'
    $shortcutMutationStarted = $true
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($startMenu)
        $shortcut.TargetPath = $viewerExe
        $shortcut.WorkingDirectory = $install
        $shortcut.Save()
        if ($StartWithWindows) { Copy-Item -LiteralPath $startMenu -Destination $startup -Force }
        elseif ($DisableStartWithWindows -and (Test-Path -LiteralPath $startup -PathType Leaf)) {
            Remove-Item -LiteralPath $startup -Force
        }
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_SHORTCUT_SETUP_FAILED: 시작 메뉴 또는 시작프로그램 바로 가기를 만들지 못했습니다. 같은 Windows 사용자로 실행하고 폴더 쓰기 권한 및 보안 정책을 확인하세요.',
            $_.Exception)
    }

    $failureCode = 'VIEWER_SMOKE_CHECK_FAILED'
    $failureDetailCode = 'VIEWER_SELF_CHECK_START_FAILED'
    Write-SswStep '새 Viewer 설치 전용 자체 점검'
    try {
        $smokeProcess = Start-Process -FilePath $viewerExe -WorkingDirectory $install `
            -ArgumentList '--install-smoke-check' -WindowStyle Hidden -PassThru -ErrorAction Stop
    }
    catch {
        throw [InvalidOperationException]::new(
            'Viewer 설치 전용 자체 점검 프로세스를 시작하지 못했습니다.',
            $_.Exception)
    }
    $failureDetailCode = 'VIEWER_SELF_CHECK_WAIT_FAILED'
    try {
        $smokeCompleted = $smokeProcess.WaitForExit(20000)
    }
    catch {
        throw [InvalidOperationException]::new(
            'Viewer 설치 전용 자체 점검 완료 여부를 확인하지 못했습니다.',
            $_.Exception)
    }
    if (-not $smokeCompleted) {
        $failureDetailCode = 'VIEWER_SELF_CHECK_TIMEOUT'
        throw 'Viewer 설치 전용 자체 점검이 20초 안에 끝나지 않았습니다.'
    }
    $failureExitCode = $smokeProcess.ExitCode
    if ($failureExitCode -ne 0) {
        $failureDetailCode = 'VIEWER_SELF_CHECK_EXITED_NONZERO'
        throw "Viewer 설치 전용 자체 점검이 실패했습니다. 종료 코드: $failureExitCode"
    }
    $smokeProcess.Dispose()
    $smokeProcess = $null
    $failureDetailCode = $null
    $failureExitCode = $null
    Write-SswStep 'Viewer 설치 전용 자체 점검을 통과했습니다.'

    # 성공 상태를 먼저 영구 기록한 뒤 백업을 정리합니다. 이후 정리 실패는 새 설치를 롤백하지 않습니다.
    $failureCode = 'VIEWER_INSTALL_COMMIT_FAILED'
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
    if (-not $DoNotStart) {
        Write-SswStep '설치된 Viewer 시작'
        try {
            Start-Process -FilePath $viewerExe -WorkingDirectory $install -ErrorAction Stop |
                Out-Null
        }
        catch {
            Write-Warning 'VIEWER_POST_START_FAILED: 설치는 완료됐지만 Viewer를 자동으로 시작하지 못했습니다. 시작 메뉴에서 Samsung Switch Watch를 실행하세요.'
            Write-Host "Viewer runtime diagnostic: $viewerRuntimeDiagnosticPath (생성된 경우)"
        }
    }
}
catch {
    $failure = $_
    if ($transactionCommitted) {
        Write-Warning "Viewer 설치는 완료됐지만 후속 정리에 실패했습니다: $($failure.Exception.Message)"
        return
    }
    if ($failure.Exception.Message -match '^(VIEWER_[A-Z0-9_]{2,63}):') {
        $failureCode = $Matches[1]
    }
    Write-Warning 'Viewer 설치 실패를 감지해 설치 전 상태로 되돌립니다.'
    Write-Host "Cause: $failureCode" -ForegroundColor Yellow
    $displayDetailCode = if ([string]::IsNullOrWhiteSpace([string]$failureDetailCode)) {
        'NOT_AVAILABLE'
    }
    else {
        $failureDetailCode
    }
    Write-Host "Detail: $displayDetailCode" -ForegroundColor Yellow
    if ($null -ne $failureExitCode) {
        Write-Host "ExitCode: $failureExitCode" -ForegroundColor Yellow
    }
    $rollbackErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'stop-new-viewer'; Action = {
            if ($smokeProcess) {
                try {
                    if (-not $smokeProcess.HasExited) {
                        $smokeProcess.Kill()
                        $smokeProcess.WaitForExit(5000) | Out-Null
                    }
                }
                finally {
                    $smokeProcess.Dispose()
                }
            }
        } },
        [pscustomobject]@{ Name = 'restore-program'; Action = {
            if ($installSwapped -and (Test-Path -LiteralPath $install)) { Remove-Item -LiteralPath $install -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $install }
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'restore-shortcuts'; Action = {
            if (-not $shortcutMutationStarted) {
                $rollbackState.ShortcutRestored = $true
                return
            }
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
            $rollbackState.ShortcutRestored = $true
        } },
        [pscustomobject]@{ Name = 'cleanup-shortcut-backup'; Action = {
            if (-not $rollbackState.ShortcutRestored) {
                throw '바로 가기 복구가 완료되지 않아 백업을 보존합니다.'
            }
            if (Test-Path -LiteralPath $shortcutBackup) { Remove-Item -LiteralPath $shortcutBackup -Recurse -Force }
        } },
        [pscustomobject]@{ Name = 'cleanup-new-shortcut-directories'; Action = {
            if (-not $rollbackState.ShortcutRestored) { return }
            if ($startupParentCreated) { Remove-SswEmptyDirectoryBestEffort -Path $startupParent }
            if ($startMenuParentCreated) { Remove-SswEmptyDirectoryBestEffort -Path $startMenuParent }
        } }
    ))
    $diagnosticCodes = @($failureCode)
    if (-not [string]::IsNullOrWhiteSpace([string]$failureDetailCode)) {
        $diagnosticCodes += $failureDetailCode
    }
    $diagnosticCodes += @($rollbackErrors)
    $diagnosticCodes = @($diagnosticCodes | Select-Object -Unique)
    try {
        Write-SswOperationJournal -Path $journalPath -Operation 'viewer-install' -TransactionId $transactionId `
            -Stage 'rollback-completed' -Status 'failed' -Version ([string]$sourceManifest.version) `
            -ErrorCodes $diagnosticCodes
    }
    catch {
        Write-Warning 'Viewer 실패 진단 기록을 저장하지 못했지만 최초 실패 원인은 유지합니다.'
    }
    if ($rollbackErrors.Count -eq 0) {
        $recovery = if ($previousInstallExisted) { 'PREVIOUS_VIEWER_RESTORED' } else { 'PARTIAL_INSTALL_REMOVED' }
        Write-Host "Recovery: $recovery" -ForegroundColor Yellow
        if ($previousInstallExisted) {
            Write-Warning '이전 Viewer 파일과 바로 가기를 복구했습니다. Viewer는 실행 중이 아니므로 시작 메뉴에서 다시 실행하세요.'
        }
    }
    else {
        Write-Host "Recovery: ROLLBACK_INCOMPLETE ($($rollbackErrors -join ', '))" -ForegroundColor Red
        Write-Warning ("일부 자동 복구 단계가 실패했습니다: {0}" -f ($rollbackErrors -join ', '))
    }
    Write-Host "Install journal: $journalPath"
    Write-Host "Viewer runtime diagnostic: $viewerRuntimeDiagnosticPath (생성된 경우)"
    throw $failure
}
}
finally {
    Exit-SswDeploymentLock -Lock $deploymentLock
}
