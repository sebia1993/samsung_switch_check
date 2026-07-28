param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory,
    [switch]$StartWithWindows,
    [switch]$DisableStartWithWindows,
    [switch]$DoNotStart,
    [switch]$Preflight,
    [switch]$PerUser,
    [switch]$MachinePhase,
    [switch]$MachineRollbackPhase,
    [string]$InstallTransactionId,
    [string]$ExpectedActiveManifestSha256
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = if ($PerUser) {
        Join-Path $env:LOCALAPPDATA 'Programs\SamsungSwitchWatch\Viewer'
    }
    else {
        Join-Path $env:ProgramFiles 'SamsungSwitchWatch\Viewer'
    }
}

$source = [IO.Path]::GetFullPath($SourceDirectory)
$install = [IO.Path]::GetFullPath($InstallDirectory)
$sourceExe = Join-Path $source 'SamsungSwitchWatch.Viewer.exe'
$sourceManifestPath = Join-Path $source 'BUILD-MANIFEST.json'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Samsung Switch Watch.lnk'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Samsung Switch Watch.lnk'
$startMenuParent = Split-Path $startMenu -Parent
$startupParent = Split-Path $startup -Parent

function Test-SswViewerAccessDeniedException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [UnauthorizedAccessException] -or
            $current -is [Security.SecurityException] -or
            ($current -is [ComponentModel.Win32Exception] -and
                $current.NativeErrorCode -eq 5)) {
            return $true
        }
        $current = $current.InnerException
    }
    return $false
}

function Assert-SswViewerSourceReadable {
    param([Parameter(Mandatory = $true)][string]$Directory)

    try {
        $directoryItem = Get-Item -LiteralPath $Directory -Force -ErrorAction Stop
        if (-not $directoryItem.PSIsContainer) {
            throw 'Viewer 패키지 원본 경로가 폴더가 아닙니다.'
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop)) {
            if ($item.PSIsContainer) { continue }
            $stream = $null
            try {
                $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
                $stream = [IO.File]::Open(
                    $item.FullName,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    $share)
            }
            finally {
                if ($stream) { $stream.Dispose() }
            }
        }
    }
    catch {
        if (Test-SswViewerAccessDeniedException -Exception $_.Exception) {
            throw [InvalidOperationException]::new(
                'VIEWER_SOURCE_ACCESS_DENIED: 승인한 관리자 계정이 압축 해제한 Viewer 원본을 읽을 수 없습니다. 관리자 계정도 읽을 수 있는 로컬 폴더에 ZIP을 다시 푼 뒤 재시도하세요.',
                $_.Exception)
        }
        throw
    }
}

function Get-SswViewerSelfCheckDetailCode {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [ComponentModel.Win32Exception]) {
            switch ([int]$current.NativeErrorCode) {
                2 { return 'FILE_MISSING' }
                3 { return 'FILE_MISSING' }
                5 { return 'VIEWER_SELF_CHECK_ACCESS_DENIED' }
                193 { return 'BAD_IMAGE' }
                216 { return 'BAD_IMAGE' }
                577 { return 'VIEWER_INSTALL_PATH_EXECUTION_BLOCKED' }
                1260 { return 'VIEWER_INSTALL_PATH_EXECUTION_BLOCKED' }
            }
        }
        if ($current -is [UnauthorizedAccessException] -or
            $current -is [Security.SecurityException]) {
            return 'VIEWER_SELF_CHECK_ACCESS_DENIED'
        }
        $current = $current.InnerException
    }
    return 'VIEWER_SELF_CHECK_START_FAILED'
}

function Invoke-SswViewerSelfCheck {
    param(
        [Parameter(Mandatory = $true)][string]$ViewerExecutable,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    if (-not (Test-Path -LiteralPath $ViewerExecutable -PathType Leaf)) {
        throw 'FILE_MISSING: 설치된 Viewer 실행 파일을 찾지 못했습니다.'
    }

    $process = $null
    try {
        try {
            $process = Start-Process -FilePath $ViewerExecutable -WorkingDirectory $WorkingDirectory `
                -ArgumentList '--install-smoke-check' -WindowStyle Hidden -PassThru -ErrorAction Stop
        }
        catch {
            $detailCode = Get-SswViewerSelfCheckDetailCode -Exception $_.Exception
            throw [InvalidOperationException]::new(
                "$detailCode`: Viewer 설치 전용 자체 점검을 시작하지 못했습니다.",
                $_.Exception)
        }

        try {
            $completed = $process.WaitForExit(20000)
        }
        catch {
            throw [InvalidOperationException]::new(
                'VIEWER_SELF_CHECK_WAIT_FAILED: Viewer 설치 전용 자체 점검 완료 여부를 확인하지 못했습니다.',
                $_.Exception)
        }
        if (-not $completed) {
            try { $process.Kill() } catch { }
            throw 'TIMEOUT: Viewer 설치 전용 자체 점검이 20초 안에 끝나지 않았습니다.'
        }
        if ($process.ExitCode -ne 0) {
            $failure = New-Object InvalidOperationException(
                "VIEWER_SELF_CHECK_EXITED_NONZERO: Viewer 설치 전용 자체 점검이 실패했습니다. 종료 코드: $($process.ExitCode)")
            $failure.Data['ExitCode'] = [int]$process.ExitCode
            throw $failure
        }
    }
    finally {
        if ($process) { $process.Dispose() }
    }
}

function Enter-SswViewerMachineDeploymentLock {
    $name = 'Global\SamsungSwitchWatch.Viewer.Machine.Deployment.v1'
    $mutex = $null
    $acquired = $false
    try {
        $security = New-Object Security.AccessControl.MutexSecurity
        $security.SetAccessRuleProtection($true, $false)
        foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
            $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
            $security.AddAccessRule((New-Object Security.AccessControl.MutexAccessRule(
                $sid,
                [Security.AccessControl.MutexRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow)))
        }
        $createdNew = $false
        $mutex = [Threading.Mutex]::new($false, $name, [ref]$createdNew, $security)
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
            throw 'DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED: 이전 Viewer 설치·제거 작업의 비정상 종료를 감지했습니다.'
        }
        if (-not $acquired) {
            throw 'DEPLOYMENT_ALREADY_RUNNING: Viewer 시스템 설치 또는 제거 작업이 이미 실행 중입니다.'
        }
        return [pscustomobject]@{ Name = $name; Mutex = $mutex }
    }
    catch {
        if ($mutex) {
            if ($acquired) { try { $mutex.ReleaseMutex() } catch { } }
            try { $mutex.Dispose() } catch { }
        }
        throw
    }
}

function Get-SswViewerMachineRollbackSlot {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $resolvedInstall = [IO.Path]::GetFullPath($InstallDirectory)
    $installParent = Split-Path $resolvedInstall -Parent
    $slot = "$resolvedInstall.__rollback"
    Assert-SswChildPath -Parent $installParent -Child $slot
    if ((Split-Path $slot -Parent) -cne $installParent) {
        throw 'VIEWER_ROLLBACK_SLOT_INVALID: Viewer rollback slot은 설치 폴더와 같은 보호된 부모에 있어야 합니다.'
    }
    return $slot
}

function Get-SswViewerRollbackTransactionPath {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $resolvedInstall = [IO.Path]::GetFullPath($InstallDirectory)
    $installParent = Split-Path $resolvedInstall -Parent
    $marker = "$resolvedInstall.__rollback-transaction.json"
    Assert-SswChildPath -Parent $installParent -Child $marker
    if ((Split-Path $marker -Parent) -cne $installParent) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 marker는 설치 폴더와 같은 보호된 부모에 있어야 합니다.'
    }
    return $marker
}

function Write-SswViewerRollbackTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$ActiveManifestSha256,
        [AllowNull()][string]$RollbackManifestSha256
    )

    if ($TransactionId -notmatch '^[0-9a-fA-F]{32}$' -or
        $ActiveManifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (-not [string]::IsNullOrWhiteSpace($RollbackManifestSha256) -and
            $RollbackManifestSha256 -notmatch '^[0-9a-fA-F]{64}$')) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 ID 또는 manifest SHA-256 형식이 올바르지 않습니다.'
    }

    $markerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $InstallDirectory
    $markerParent = Split-Path $markerPath -Parent
    Assert-SswTrustedDirectoryRootOwner -Path $markerParent | Out-Null
    if (Test-Path -LiteralPath $markerPath) {
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 marker가 일반 파일이 아닙니다.'
        }
        try {
            Assert-SswAdministratorsOnlyFileAcl -Path $markerPath
        }
        catch {
            throw [InvalidOperationException]::new(
                'VIEWER_ROLLBACK_TRANSACTION_TRUST_INVALID: 기존 rollback 작업 marker를 신뢰할 수 없어 덮어쓰지 않았습니다.',
                $_.Exception)
        }
    }

    $payload = [ordered]@{
        formatVersion = 1
        product = 'SamsungSwitchWatch'
        operation = 'viewer-install-rollback'
        transactionId = $TransactionId.ToLowerInvariant()
        activeManifestSha256 = $ActiveManifestSha256.ToLowerInvariant()
        rollbackManifestSha256 = if (
            [string]::IsNullOrWhiteSpace($RollbackManifestSha256)) {
            $null
        }
        else {
            $RollbackManifestSha256.ToLowerInvariant()
        }
    } | ConvertTo-Json -Depth 3
    $temporary = "$markerPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $replaceBackup = "$markerPath.$([Guid]::NewGuid().ToString('N')).bak"
    Assert-SswChildPath -Parent $markerParent -Child $temporary
    Assert-SswChildPath -Parent $markerParent -Child $replaceBackup
    try {
        $utf8 = New-Object Text.UTF8Encoding($false)
        $payloadBytes = $utf8.GetBytes($payload)
        $stream = [IO.File]::Open(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $stream.Write($payloadBytes, 0, $payloadBytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        Set-SswAdministratorsOnlyFileAcl -Path $temporary
        Assert-SswAdministratorsOnlyFileAcl -Path $temporary
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            [IO.File]::Replace($temporary, $markerPath, $replaceBackup, $true)
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $markerPath
        }
        Set-SswAdministratorsOnlyFileAcl -Path $markerPath
        Assert-SswAdministratorsOnlyFileAcl -Path $markerPath
    }
    finally {
        foreach ($artifact in @($temporary, $replaceBackup)) {
            if (Test-Path -LiteralPath $artifact -PathType Leaf) {
                Remove-Item -LiteralPath $artifact -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Read-SswViewerRollbackTransaction {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $markerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $InstallDirectory
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_MISSING: 현재 설치 작업의 rollback marker가 없어 자동 복구를 중단했습니다.'
    }
    try {
        Assert-SswAdministratorsOnlyFileAcl -Path $markerPath
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_ROLLBACK_TRANSACTION_TRUST_INVALID: rollback 작업 marker의 소유권 또는 ACL을 신뢰할 수 없습니다.',
            $_.Exception)
    }
    $markerItem = Get-Item -LiteralPath $markerPath -Force
    if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $markerItem.Length -gt 8192) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 marker 형식이나 크기가 올바르지 않습니다.'
    }
    try {
        $markerText = [IO.File]::ReadAllText(
            $markerPath,
            (New-Object Text.UTF8Encoding($false, $true)))
        $marker = $markerText | ConvertFrom-Json
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 marker를 UTF-8 JSON으로 읽지 못했습니다.',
            $_.Exception)
    }
    if ($null -eq $marker -or
        $marker -is [Array] -or
        $marker -isnot [pscustomobject]) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 marker는 단일 JSON object여야 합니다.'
    }
    $propertyNames = @($marker.PSObject.Properties | ForEach-Object { $_.Name })
    $expectedProperties = @(
        'formatVersion',
        'product',
        'operation',
        'transactionId',
        'activeManifestSha256',
        'rollbackManifestSha256')
    $validFormatVersion = (
        $marker.formatVersion -is [int] -and
        [int]$marker.formatVersion -eq 1)
    if ($propertyNames.Count -ne $expectedProperties.Count -or
        @($expectedProperties | Where-Object { $_ -notin $propertyNames }).Count -ne 0 -or
        -not $validFormatVersion -or
        [string]$marker.product -cne 'SamsungSwitchWatch' -or
        [string]$marker.operation -cne 'viewer-install-rollback' -or
        [string]$marker.transactionId -notmatch '^[0-9a-f]{32}$' -or
        [string]$marker.activeManifestSha256 -notmatch '^[0-9a-f]{64}$' -or
        (-not [string]::IsNullOrWhiteSpace([string]$marker.rollbackManifestSha256) -and
            [string]$marker.rollbackManifestSha256 -notmatch '^[0-9a-f]{64}$')) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 작업 marker의 제품, 필드 또는 값이 올바르지 않습니다.'
    }
    return $marker
}

function Confirm-SswViewerRollbackTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$ExpectedActiveManifestSha256
    )

    if ($TransactionId -notmatch '^[0-9a-fA-F]{32}$' -or
        $ExpectedActiveManifestSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback 요청의 작업 ID 또는 manifest SHA-256 형식이 올바르지 않습니다.'
    }
    $marker = Read-SswViewerRollbackTransaction -InstallDirectory $InstallDirectory
    if ([string]$marker.transactionId -cne $TransactionId.ToLowerInvariant() -or
        [string]$marker.activeManifestSha256 -cne
            $ExpectedActiveManifestSha256.ToLowerInvariant()) {
        throw 'VIEWER_ROLLBACK_ACTIVE_CHANGED: 다른 Viewer 설치 작업이 활성화되어 이전 작업의 rollback을 중단했습니다.'
    }

    if (Test-Path -LiteralPath $InstallDirectory -PathType Container) {
        try {
            $activePackage = Get-SswValidatedViewerPackage -Directory $InstallDirectory `
                -ManifestPath (Join-Path $InstallDirectory 'BUILD-MANIFEST.json')
            if ($activePackage.ManifestSha256 -ne [string]$marker.activeManifestSha256) {
                throw 'VIEWER_ROLLBACK_ACTIVE_CHANGED: 현재 Viewer가 rollback 요청 작업의 설치본과 일치하지 않습니다.'
            }
        }
        catch {
            if ($_.Exception.Message -like 'VIEWER_ROLLBACK_ACTIVE_CHANGED:*') { throw }
            # marker가 작업 소유권을 입증하므로 EDR 격리나 손상된 현재 설치도
            # 검증된 rollback slot로 복구할 수 있습니다.
        }
    }

    $rollbackSlot = Get-SswViewerMachineRollbackSlot `
        -InstallDirectory $InstallDirectory
    $markerRollbackHash = [string]$marker.rollbackManifestSha256
    if ([string]::IsNullOrWhiteSpace($markerRollbackHash)) {
        if (Test-Path -LiteralPath $rollbackSlot) {
            throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: 첫 설치 rollback marker와 달리 rollback slot이 존재합니다.'
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $rollbackSlot -PathType Container)) {
            throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback marker가 지정한 이전 Viewer slot이 없습니다.'
        }
        $rollbackPackage = Get-SswValidatedViewerRollbackPackage `
            -RollbackSlot $rollbackSlot
        if ($rollbackPackage.ManifestSha256 -ne $markerRollbackHash) {
            throw 'VIEWER_ROLLBACK_TRANSACTION_INVALID: rollback slot이 작업 marker의 이전 Viewer와 일치하지 않습니다.'
        }
    }

}

function Remove-SswViewerRollbackTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$ExpectedActiveManifestSha256
    )

    $marker = Read-SswViewerRollbackTransaction -InstallDirectory $InstallDirectory
    if ([string]$marker.transactionId -cne $TransactionId.ToLowerInvariant() -or
        [string]$marker.activeManifestSha256 -cne
            $ExpectedActiveManifestSha256.ToLowerInvariant()) {
        throw 'VIEWER_ROLLBACK_ACTIVE_CHANGED: rollback 완료 전 작업 marker가 바뀌어 제거하지 않았습니다.'
    }
    $markerPath = Get-SswViewerRollbackTransactionPath `
        -InstallDirectory $InstallDirectory
    Remove-Item -LiteralPath $markerPath -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $markerPath) {
        throw 'VIEWER_ROLLBACK_TRANSACTION_CONSUME_FAILED: 완료된 rollback 작업 marker를 제거하지 못했습니다.'
    }
}

function Get-SswValidatedViewerRollbackPackage {
    param([Parameter(Mandatory = $true)][string]$RollbackSlot)

    if (-not (Test-Path -LiteralPath $RollbackSlot -PathType Container)) {
        throw 'VIEWER_ROLLBACK_SLOT_INVALID: 보존된 Viewer rollback slot이 폴더가 아닙니다.'
    }
    Assert-SswTrustedDirectoryRootOwner -Path $RollbackSlot | Out-Null
    return Get-SswValidatedViewerPackage -Directory $RollbackSlot `
        -ManifestPath (Join-Path $RollbackSlot 'BUILD-MANIFEST.json')
}

function Initialize-SswViewerMachineRollbackSlot {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $rollbackSlot = Get-SswViewerMachineRollbackSlot -InstallDirectory $InstallDirectory
    if (-not (Test-Path -LiteralPath $rollbackSlot)) { return $rollbackSlot }

    $rollbackPackage = Get-SswValidatedViewerRollbackPackage -RollbackSlot $rollbackSlot
    if (-not (Test-Path -LiteralPath $InstallDirectory)) {
        Move-Item -LiteralPath $rollbackSlot -Destination $InstallDirectory
        $restoredPackage = Get-SswValidatedViewerPackage -Directory $InstallDirectory `
            -ManifestPath (Join-Path $InstallDirectory 'BUILD-MANIFEST.json')
        if ($restoredPackage.ManifestSha256 -ne $rollbackPackage.ManifestSha256) {
            throw 'VIEWER_ROLLBACK_RESTORE_INVALID: 중단된 이전 설치의 rollback slot 복원 결과가 일치하지 않습니다.'
        }
        Write-SswStep '중단된 이전 설치의 검증된 Viewer rollback slot 복원 완료'
        return $rollbackSlot
    }

    try {
        $currentPackage = Get-SswValidatedViewerPackage -Directory $InstallDirectory `
            -ManifestPath (Join-Path $InstallDirectory 'BUILD-MANIFEST.json')
        Assert-SswTrustedDirectoryRootOwner -Path $InstallDirectory | Out-Null
    }
    catch {
        $recovery = Invoke-SswViewerMachineRollbackCore -InstallDirectory $InstallDirectory
        if ($recovery -cne 'PREVIOUS_VIEWER_RESTORED') {
            throw "VIEWER_ROLLBACK_RESTORE_INVALID: 손상된 현재 설치의 rollback 복구 결과가 올바르지 않습니다: $recovery"
        }
        Write-SswStep '손상된 현재 설치 대신 검증된 Viewer rollback slot 복원 완료'
        return $rollbackSlot
    }
    try {
        Invoke-SswViewerSelfCheck `
            -ViewerExecutable (Join-Path $InstallDirectory 'SamsungSwitchWatch.Viewer.exe') `
            -WorkingDirectory $InstallDirectory
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_CURRENT_SELF_CHECK_FAILED: 현재 Viewer 실행 점검이 실패했습니다. 현재 설치와 rollback slot을 모두 보존했습니다.',
            $_.Exception)
    }

    # 정상 현재 설치와 한 세대 전 slot을 모두 staging 검증과 프로세스 종료까지
    # 보존합니다. 실제 교체 직전에만 slot을 현재 버전으로 회전합니다.
    Write-SswStep (
        "검증된 현재 Viewer $([string]$currentPackage.Manifest.version)을(를) " +
        '다음 rollback 기준으로 준비')
    return $rollbackSlot
}

function Move-SswViewerCurrentInstallToRollbackSlot {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][ref]$MovedToRollbackSlot
    )

    $MovedToRollbackSlot.Value = $false
    $rollbackSlot = Get-SswViewerMachineRollbackSlot -InstallDirectory $InstallDirectory
    $previousPackage = Get-SswValidatedViewerPackage -Directory $InstallDirectory `
        -ManifestPath (Join-Path $InstallDirectory 'BUILD-MANIFEST.json')
    Assert-SswTrustedDirectoryRootOwner -Path $InstallDirectory | Out-Null

    if (Test-Path -LiteralPath $rollbackSlot) {
        Get-SswValidatedViewerRollbackPackage -RollbackSlot $rollbackSlot | Out-Null
        Remove-Item -LiteralPath $rollbackSlot -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $rollbackSlot) {
            throw 'VIEWER_ROLLBACK_SLOT_REFRESH_FAILED: 오래된 Viewer rollback slot을 비우지 못했습니다.'
        }
    }

    Move-Item -LiteralPath $InstallDirectory -Destination $rollbackSlot -ErrorAction Stop
    $MovedToRollbackSlot.Value = $true
    $rollbackPackage = Get-SswValidatedViewerRollbackPackage -RollbackSlot $rollbackSlot
    if ($rollbackPackage.ManifestSha256 -ne $previousPackage.ManifestSha256) {
        throw 'VIEWER_ROLLBACK_SLOT_INVALID: 이동한 이전 Viewer가 검증된 설치와 일치하지 않습니다.'
    }
    return $rollbackPackage
}

function Invoke-SswViewerMachineRollbackCore {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $resolvedInstall = [IO.Path]::GetFullPath($InstallDirectory)
    $installParent = Split-Path $resolvedInstall -Parent
    Assert-SswTrustedDirectoryRootOwner -Path $installParent | Out-Null
    $rollbackSlot = Get-SswViewerMachineRollbackSlot -InstallDirectory $resolvedInstall
    $rollbackPackage = $null
    if (Test-Path -LiteralPath $rollbackSlot) {
        $rollbackPackage = Get-SswValidatedViewerRollbackPackage -RollbackSlot $rollbackSlot
    }

    $currentInstallPresent = Test-Path -LiteralPath $resolvedInstall
    if ($currentInstallPresent) {
        Assert-SswNoReparsePoint -Parent $installParent -Child $resolvedInstall
    }
    if (-not $currentInstallPresent -and $null -eq $rollbackPackage) {
        return 'NO_MACHINE_INSTALL_PRESENT'
    }

    $viewerProcesses = @(Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue)
    if ($viewerProcesses.Count -gt 0) {
        $viewerProcesses | Stop-Process
        foreach ($process in $viewerProcesses) {
            try { $process.WaitForExit(5000) | Out-Null } catch { }
        }
        if (Get-Process -Name 'SamsungSwitchWatch.Viewer' -ErrorAction SilentlyContinue) {
            throw 'VIEWER_ROLLBACK_PROCESS_STOP_FAILED: Viewer 프로세스를 종료하지 못해 rollback을 중단했습니다.'
        }
    }

    $quarantine = "$resolvedInstall.__failed_$([Guid]::NewGuid().ToString('N'))"
    Assert-SswChildPath -Parent $installParent -Child $quarantine
    $currentQuarantined = $false
    $rollbackMoved = $false
    try {
        # 현재 설치는 바로 가기 실행 실패, EDR 격리 또는 파일 손상 상태일 수 있으므로
        # 패키지 검증을 복구의 선행 조건으로 삼지 않는다. 보호된 정확한 경로를 먼저
        # 격리한 뒤, 별도로 검증한 rollback slot만 활성 경로로 복원한다.
        if ($currentInstallPresent) {
            Move-Item -LiteralPath $resolvedInstall -Destination $quarantine -ErrorAction Stop
            $currentQuarantined = $true
        }

        if ($null -ne $rollbackPackage) {
            Move-Item -LiteralPath $rollbackSlot -Destination $resolvedInstall -ErrorAction Stop
            $rollbackMoved = $true
            $restoredPackage = Get-SswValidatedViewerPackage -Directory $resolvedInstall `
                -ManifestPath (Join-Path $resolvedInstall 'BUILD-MANIFEST.json')
            if ($restoredPackage.ManifestSha256 -ne $rollbackPackage.ManifestSha256) {
                throw 'VIEWER_ROLLBACK_RESTORE_INVALID: 복원된 Viewer가 검증된 rollback slot과 일치하지 않습니다.'
            }
        }

        if ($currentQuarantined -and (Test-Path -LiteralPath $quarantine)) {
            try {
                Remove-Item -LiteralPath $quarantine -Recurse -Force -ErrorAction Stop
            }
            catch {
                Write-Warning 'VIEWER_FAILED_INSTALL_QUARANTINE_PRESERVED: 이전의 실패한 Viewer 설치 격리 폴더를 정리하지 못했지만 검증된 버전 복원은 완료했습니다.'
            }
        }
        return $(if ($null -ne $rollbackPackage) {
            'PREVIOUS_VIEWER_RESTORED'
        }
        else {
            'PARTIAL_INSTALL_REMOVED'
        })
    }
    catch {
        $rollbackFailure = $_
        if ($rollbackMoved -and
            (Test-Path -LiteralPath $resolvedInstall) -and
            -not (Test-Path -LiteralPath $rollbackSlot)) {
            try {
                Move-Item -LiteralPath $resolvedInstall -Destination $rollbackSlot -ErrorAction Stop
                $rollbackMoved = $false
            }
            catch { }
        }
        if ($currentQuarantined -and
            (Test-Path -LiteralPath $quarantine) -and
            -not (Test-Path -LiteralPath $resolvedInstall)) {
            try { Move-Item -LiteralPath $quarantine -Destination $resolvedInstall } catch { }
        }
        throw [InvalidOperationException]::new(
            'VIEWER_MACHINE_ROLLBACK_INCOMPLETE: Viewer rollback을 완료하지 못했습니다. rollback slot과 격리 폴더를 삭제하지 마세요.',
            $rollbackFailure.Exception)
    }
}

function Invoke-SswViewerMachineRollbackPhase {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$InstallTransactionId,
        [Parameter(Mandatory = $true)][string]$ExpectedActiveManifestSha256
    )

    Assert-SswAdministrator
    $lock = Enter-SswViewerMachineDeploymentLock
    try {
        Confirm-SswViewerRollbackTransaction -InstallDirectory $InstallDirectory `
            -TransactionId $InstallTransactionId `
            -ExpectedActiveManifestSha256 $ExpectedActiveManifestSha256
        $recovery = Invoke-SswViewerMachineRollbackCore `
            -InstallDirectory $InstallDirectory
        Remove-SswViewerRollbackTransaction -InstallDirectory $InstallDirectory `
            -TransactionId $InstallTransactionId `
            -ExpectedActiveManifestSha256 $ExpectedActiveManifestSha256
        return $recovery
    }
    finally {
        Exit-SswDeploymentLock -Lock $lock
    }
}

function Invoke-SswViewerUserIntegration {
    param(
        [Parameter(Mandatory = $true)][string]$ViewerExecutable,
        [switch]$EnableStartup,
        [switch]$DisableStartup
    )

    $integrationId = [Guid]::NewGuid().ToString('N')
    $backupRoot = Join-Path ([IO.Path]::GetTempPath()) "SamsungSwitchWatch-Viewer-links-$integrationId"
    $startMenuWasPresent = Test-Path -LiteralPath $startMenu -PathType Leaf
    $startupWasPresent = Test-Path -LiteralPath $startup -PathType Leaf
    $startParentMade = $false
    $startupParentMade = $false
    $mutationStarted = $false
    try {
        New-Item -ItemType Directory -Path $backupRoot | Out-Null
        if ($startMenuWasPresent) {
            Copy-Item -LiteralPath $startMenu -Destination (Join-Path $backupRoot 'start-menu.lnk')
        }
        if ($startupWasPresent) {
            Copy-Item -LiteralPath $startup -Destination (Join-Path $backupRoot 'startup.lnk')
        }

        $startParentMade = New-SswDirectoryIfMissing -Path $startMenuParent `
            -FailureCode 'VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE' -Description '시작 메뉴'
        $keepStartup = $EnableStartup -or ($startupWasPresent -and -not $DisableStartup)
        if ($keepStartup) {
            $startupParentMade = New-SswDirectoryIfMissing -Path $startupParent `
                -FailureCode 'VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE' -Description '시작프로그램'
        }

        $mutationStarted = $true
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($startMenu)
        $shortcut.TargetPath = $ViewerExecutable
        $shortcut.WorkingDirectory = Split-Path $ViewerExecutable -Parent
        $shortcut.Save()
        if ($keepStartup) {
            Copy-Item -LiteralPath $startMenu -Destination $startup -Force
        }
        elseif ($DisableStartup -and (Test-Path -LiteralPath $startup -PathType Leaf)) {
            Remove-Item -LiteralPath $startup -Force
        }
    }
    catch {
        $failure = $_
        if ($mutationStarted) {
            foreach ($link in @($startMenu, $startup)) {
                if (Test-Path -LiteralPath $link -PathType Leaf) {
                    Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
                }
            }
            if ($startMenuWasPresent -and
                (Test-Path -LiteralPath (Join-Path $backupRoot 'start-menu.lnk') -PathType Leaf)) {
                Copy-Item -LiteralPath (Join-Path $backupRoot 'start-menu.lnk') `
                    -Destination $startMenu -Force -ErrorAction SilentlyContinue
            }
            if ($startupWasPresent -and
                (Test-Path -LiteralPath (Join-Path $backupRoot 'startup.lnk') -PathType Leaf)) {
                Copy-Item -LiteralPath (Join-Path $backupRoot 'startup.lnk') `
                    -Destination $startup -Force -ErrorAction SilentlyContinue
            }
        }
        if ($startupParentMade) { Remove-SswEmptyDirectoryBestEffort -Path $startupParent }
        if ($startParentMade) { Remove-SswEmptyDirectoryBestEffort -Path $startMenuParent }
        throw [InvalidOperationException]::new(
            'VIEWER_SHORTCUT_SETUP_FAILED: 현재 사용자 바로 가기 또는 자동 시작을 구성하지 못했습니다.',
            $failure.Exception)
    }
    finally {
        if (Test-Path -LiteralPath $backupRoot) {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

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

function ConvertTo-SswPowerShellLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-SswViewerElevatedMachinePhase {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$PackageSource,
        [Parameter(Mandatory = $true)][string]$MachineInstallDirectory,
        [Parameter(Mandatory = $true)][string]$InstallTransactionId
    )

    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellPath -PathType Leaf)) {
        throw 'VIEWER_POWERSHELL_NOT_FOUND: Windows PowerShell을 찾지 못했습니다.'
    }

    $installerLiteral = ConvertTo-SswPowerShellLiteral -Value $InstallerPath
    $sourceLiteral = ConvertTo-SswPowerShellLiteral -Value $PackageSource
    $installLiteral = ConvertTo-SswPowerShellLiteral -Value $MachineInstallDirectory
    $transactionLiteral = ConvertTo-SswPowerShellLiteral -Value $InstallTransactionId
    $command = @"
try {
    & $installerLiteral -MachinePhase -SourceDirectory $sourceLiteral -InstallDirectory $installLiteral -InstallTransactionId $transactionLiteral
    exit 0
}
catch {
    Write-Host ''
    Write-Host ('Viewer machine installation failed: ' + `$_.Exception.Message) -ForegroundColor Red
    `$current = `$_.Exception
    while (`$null -ne `$current) {
        if (`$current -is [UnauthorizedAccessException] -or
            `$current -is [Security.SecurityException] -or
            (`$current -is [ComponentModel.Win32Exception] -and `$current.NativeErrorCode -eq 5)) {
            exit 41
        }
        `$current = `$current.InnerException
    }
    if (`$_.Exception.Message -like 'VIEWER_SOURCE_ACCESS_DENIED:*') { exit 41 }
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    try {
        $process = Start-Process -FilePath $powerShellPath -Verb RunAs -Wait -PassThru `
            -ArgumentList "-NoLogo -NoProfile -EncodedCommand $encoded" -ErrorAction Stop
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_ELEVATION_NOT_GRANTED: 관리자 승격을 승인하지 않았거나 승격된 Windows PowerShell을 시작하지 못했습니다.',
            $_.Exception)
    }

    if ($process.ExitCode -eq 41) {
        throw 'VIEWER_SOURCE_ACCESS_DENIED: 승인한 관리자 계정이 압축 해제한 Viewer 원본을 읽을 수 없습니다. 관리자 계정도 읽을 수 있는 로컬 폴더에 ZIP을 다시 푼 뒤 재시도하세요.'
    }
    if ($process.ExitCode -ne 0) {
        throw "VIEWER_MACHINE_PHASE_FAILED: 관리자 설치 단계가 실패했습니다. 승격된 창에 표시된 안전 진단 코드를 확인하세요. 종료 코드: $($process.ExitCode)"
    }
}

function Invoke-SswViewerElevatedRollbackPhase {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$MachineInstallDirectory,
        [Parameter(Mandatory = $true)][string]$InstallTransactionId,
        [Parameter(Mandatory = $true)][string]$ExpectedActiveManifestSha256
    )

    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellPath -PathType Leaf)) {
        throw 'VIEWER_POWERSHELL_NOT_FOUND: Windows PowerShell을 찾지 못했습니다.'
    }
    $installerLiteral = ConvertTo-SswPowerShellLiteral -Value $InstallerPath
    $installLiteral = ConvertTo-SswPowerShellLiteral -Value $MachineInstallDirectory
    $transactionLiteral = ConvertTo-SswPowerShellLiteral -Value $InstallTransactionId
    $expectedHashLiteral = ConvertTo-SswPowerShellLiteral `
        -Value $ExpectedActiveManifestSha256
    $command = @"
try {
    `$recovery = & $installerLiteral -MachineRollbackPhase -InstallDirectory $installLiteral -InstallTransactionId $transactionLiteral -ExpectedActiveManifestSha256 $expectedHashLiteral
    Write-Host ('Recovery: ' + `$recovery) -ForegroundColor Yellow
    exit 0
}
catch {
    Write-Host 'Recovery: ROLLBACK_INCOMPLETE' -ForegroundColor Red
    Write-Host (`$_.Exception.Message) -ForegroundColor Red
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    try {
        $process = Start-Process -FilePath $powerShellPath -Verb RunAs -Wait -PassThru `
            -ArgumentList "-NoLogo -NoProfile -EncodedCommand $encoded" -ErrorAction Stop
    }
    catch {
        throw [InvalidOperationException]::new(
            'VIEWER_ROLLBACK_ELEVATION_NOT_GRANTED: rollback 관리자 승격이 취소되었거나 시작되지 않았습니다.',
            $_.Exception)
    }
    if ($process.ExitCode -ne 0) {
        throw "VIEWER_MACHINE_ROLLBACK_INCOMPLETE: 승격된 rollback 단계가 완료되지 않았습니다. 종료 코드: $($process.ExitCode)"
    }
}

function Preserve-SswLegacyViewerInstall {
    param(
        [Parameter(Mandatory = $true)][string]$LegacyDirectory,
        [Parameter(Mandatory = $true)][string]$CurrentSource
    )

    if (-not (Test-Path -LiteralPath $LegacyDirectory)) { return }
    Assert-SswProductPath -Path $LegacyDirectory -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer' -RequireExactProductRoot
    if (-not (Test-Path -LiteralPath $LegacyDirectory -PathType Container)) {
        Write-Warning 'VIEWER_LEGACY_INSTALL_PRESERVED_UNVERIFIED: 이전 사용자 설치 경로가 폴더가 아니므로 그대로 보존합니다.'
        return
    }
    $legacyPrefix = $LegacyDirectory.TrimEnd('\') + '\'
    if ($CurrentSource.Equals($LegacyDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $CurrentSource.StartsWith($legacyPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning 'VIEWER_LEGACY_INSTALL_PRESERVED_IN_USE: 현재 설치 원본이 이전 사용자 설치 폴더 안에 있어 자동 삭제하지 않습니다.'
        return
    }

    try {
        $legacyManifestPath = Join-Path $LegacyDirectory 'BUILD-MANIFEST.json'
        $null = Get-SswValidatedViewerPackage -Directory $LegacyDirectory `
            -ManifestPath $legacyManifestPath
    }
    catch {
        Write-Warning 'VIEWER_LEGACY_INSTALL_PRESERVED_UNVERIFIED: 이전 사용자 설치의 제품 identity를 검증하지 못해 폴더와 사용자 데이터를 모두 보존합니다.'
        return
    }

    Write-Warning 'VIEWER_LEGACY_INSTALL_PRESERVED_RECOVERABLE: 검증된 이전 사용자별 프로그램 폴더를 자동 삭제하지 않고 복구용으로 보존합니다.'
}

Write-SswStep 'Viewer 설치 전 검사'
if ($env:OS -ne 'Windows_NT') { throw 'Viewer는 Windows x64에서만 설치할 수 있습니다.' }
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'VIEWER_UNSUPPORTED_ARCHITECTURE: Viewer는 64비트 Windows에서만 설치할 수 있습니다.'
}
if ($StartWithWindows -and $DisableStartWithWindows) {
    throw '-StartWithWindows와 -DisableStartWithWindows는 동시에 사용할 수 없습니다.'
}
$validInstallTransactionId = (
    -not [string]::IsNullOrWhiteSpace($InstallTransactionId) -and
    $InstallTransactionId -match '^[0-9a-fA-F]{32}$')
$validExpectedActiveHash = (
    -not [string]::IsNullOrWhiteSpace($ExpectedActiveManifestSha256) -and
    $ExpectedActiveManifestSha256 -match '^[0-9a-fA-F]{64}$')
if (($MachinePhase -and (
        $PerUser -or $MachineRollbackPhase -or $Preflight -or
        $StartWithWindows -or $DisableStartWithWindows -or $DoNotStart -or
        -not $validInstallTransactionId -or
        -not [string]::IsNullOrWhiteSpace($ExpectedActiveManifestSha256))) -or
    ($MachineRollbackPhase -and (
        $PerUser -or $Preflight -or $StartWithWindows -or
        $DisableStartWithWindows -or $DoNotStart -or
        -not $validInstallTransactionId -or
        -not $validExpectedActiveHash)) -or
    (-not $MachinePhase -and -not $MachineRollbackPhase -and (
        -not [string]::IsNullOrWhiteSpace($InstallTransactionId) -or
        -not [string]::IsNullOrWhiteSpace($ExpectedActiveManifestSha256)))) {
    throw 'VIEWER_INSTALL_MODE_INVALID: 내부 machine phase 옵션 또는 -PerUser 조합이 올바르지 않습니다.'
}
if ($PerUser) {
    Assert-SswProductPath -Path $install -BaseRoot $env:LOCALAPPDATA `
        -ProductRelativeRoot 'Programs\SamsungSwitchWatch\Viewer'
}
else {
    Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles `
        -ProductRelativeRoot 'SamsungSwitchWatch\Viewer'
}
if ($MachineRollbackPhase) {
    $machineRecovery = Invoke-SswViewerMachineRollbackPhase -InstallDirectory $install `
        -InstallTransactionId $InstallTransactionId `
        -ExpectedActiveManifestSha256 $ExpectedActiveManifestSha256
    return $machineRecovery
}

Assert-SswViewerSourceReadable -Directory $source
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "VIEWER_PACKAGE_FILE_MISSING: Viewer 배포 파일을 찾지 못했습니다: $sourceExe"
}
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
    throw "VIEWER_PACKAGE_FILE_MISSING: 패키지 빌드 매니페스트를 찾지 못했습니다: $sourceManifestPath"
}
$sourcePackage = Get-SswValidatedViewerPackage -Directory $source -ManifestPath $sourceManifestPath
$sourceManifest = $sourcePackage.Manifest
if ($source.TrimEnd('\') -eq $install.TrimEnd('\')) { throw '배포 ZIP을 설치 대상 폴더 밖에서 실행하세요.' }

Write-Host "  source  : $source"
Write-Host "  install : $install"
if ($Preflight) {
    Write-SswStep '사전 검사를 통과했습니다. 시스템은 변경되지 않았습니다.'
    return
}

if (-not $PerUser -and -not $MachinePhase) {
    if (Test-SswAdministrator) {
        throw 'VIEWER_ORIGINAL_USER_PHASE_REQUIRES_UNELEVATED: 기본 설치는 현재 사용자의 바로 가기와 자동 시작을 올바르게 구성하도록 승격되지 않은 창에서 시작해야 합니다.'
    }
    $outerInstallTransactionId = [Guid]::NewGuid().ToString('N')
    Write-SswStep '관리자 권한으로 Viewer 시스템 프로그램 설치'
    Invoke-SswViewerElevatedMachinePhase -InstallerPath $PSCommandPath `
        -PackageSource $source -MachineInstallDirectory $install `
        -InstallTransactionId $outerInstallTransactionId

    $viewerExe = Join-Path $install 'SamsungSwitchWatch.Viewer.exe'
    try {
        Write-SswStep '원래 Windows 사용자 권한으로 설치 경로 실행 확인'
        Invoke-SswViewerSelfCheck -ViewerExecutable $viewerExe -WorkingDirectory $install
        Invoke-SswViewerUserIntegration -ViewerExecutable $viewerExe `
            -EnableStartup:$StartWithWindows -DisableStartup:$DisableStartWithWindows
    }
    catch {
        $userPhaseFailure = $_
        Write-Warning 'VIEWER_USER_PHASE_FAILED: 원래 사용자 실행 확인 또는 바로 가기 설정이 실패해 이전 시스템 설치 복원을 요청합니다.'
        try {
            Invoke-SswViewerElevatedRollbackPhase -InstallerPath $PSCommandPath `
                -MachineInstallDirectory $install `
                -InstallTransactionId $outerInstallTransactionId `
                -ExpectedActiveManifestSha256 $sourcePackage.ManifestSha256
            Write-Host 'Recovery: MACHINE_ROLLBACK_COMPLETED' -ForegroundColor Yellow
        }
        catch {
            Write-Host 'Recovery: ROLLBACK_INCOMPLETE' -ForegroundColor Red
            Write-Warning 'VIEWER_MACHINE_ROLLBACK_INCOMPLETE: rollback slot은 보존했습니다. 새 설치와 rollback slot을 수동 삭제하지 말고 관리자에게 확인하세요.'
            throw [InvalidOperationException]::new(
                'VIEWER_USER_PHASE_FAILED_ROLLBACK_INCOMPLETE: 사용자 단계와 자동 rollback이 모두 완료되지 않았습니다.',
                $userPhaseFailure.Exception)
        }
        throw $userPhaseFailure
    }

    $legacyInstall = Join-Path $env:LOCALAPPDATA 'Programs\SamsungSwitchWatch\Viewer'
    Preserve-SswLegacyViewerInstall -LegacyDirectory $legacyInstall -CurrentSource $source
    Write-SswStep "Viewer 시스템 설치 및 현재 사용자 통합 완료: $viewerExe"
    if (-not $MachinePhase -and -not $DoNotStart) {
        try {
            Start-Process -FilePath $viewerExe -WorkingDirectory $install -ErrorAction Stop | Out-Null
        }
        catch {
            $postStartDetail = Get-SswViewerSelfCheckDetailCode -Exception $_.Exception
            Write-Warning "VIEWER_POST_START_FAILED: 설치는 완료됐지만 Viewer를 자동으로 시작하지 못했습니다. Detail: $postStartDetail"
        }
    }
    return
}

if ($MachinePhase) { Assert-SswAdministrator }
$deploymentLock = if ($MachinePhase) {
    Enter-SswViewerMachineDeploymentLock
}
else {
    Enter-SswDeploymentLock -Product 'Viewer'
}
try {
$installParent = Split-Path $install -Parent
$transactionId = [Guid]::NewGuid().ToString('N')
$staging = "$install.__staging_$transactionId"
$backup = if ($MachinePhase) {
    Get-SswViewerMachineRollbackSlot -InstallDirectory $install
}
else {
    "$install.__backup_$transactionId"
}
$shortcutBackup = Join-Path ([IO.Path]::GetTempPath()) "SamsungSwitchWatch-Viewer-$transactionId"
$journalPath = if ($MachinePhase) {
    Join-Path $env:ProgramData 'SamsungSwitchWatch-Viewer-Operations\viewer-install.json'
}
else {
    Join-Path $env:LOCALAPPDATA 'SamsungSwitchWatch-Operations\viewer-install.json'
}
$installSwapped = $false
$shortcutBackupsReady = $false
$shortcutMutationStarted = $false
$rollbackState = [pscustomobject]@{ ShortcutRestored = $false }
$startMenuParentCreated = $false
$startupParentCreated = $false
$transactionCommitted = $false
$previousInstallMovedToBackup = $false
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
    if ($MachinePhase) {
        $backup = Initialize-SswViewerMachineRollbackSlot -InstallDirectory $install
        $previousInstallExisted = Test-Path -LiteralPath $install -PathType Container
    }
    Write-SswStep '검증된 임시 폴더에 Viewer 배포 파일 준비'
    New-Item -ItemType Directory -Path $installParent, $staging -Force | Out-Null
    if ($MachinePhase) {
        Assert-SswTrustedDirectoryRootOwner -Path $installParent
    }
    if (-not $MachinePhase) {
        New-Item -ItemType Directory -Path $shortcutBackup -Force | Out-Null
    }
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
    if (-not $MachinePhase) {
        if ($startMenuExisted) { Copy-Item -LiteralPath $startMenu -Destination (Join-Path $shortcutBackup 'start-menu.lnk') -Force }
        if ($startupExisted) { Copy-Item -LiteralPath $startup -Destination (Join-Path $shortcutBackup 'startup.lnk') -Force }
        $shortcutBackupsReady = $true
    }

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
    if (Test-Path -LiteralPath $install) {
        if ($MachinePhase) {
            Move-SswViewerCurrentInstallToRollbackSlot -InstallDirectory $install `
                -MovedToRollbackSlot ([ref]$previousInstallMovedToBackup) | Out-Null
        }
        else {
            Move-Item -LiteralPath $install -Destination $backup
            $previousInstallMovedToBackup = $true
        }
    }
    Move-Item -LiteralPath $staging -Destination $install
    $installSwapped = $true

    $viewerExe = Join-Path $install 'SamsungSwitchWatch.Viewer.exe'
    $failureCode = 'VIEWER_INSTALLED_PACKAGE_INVALID'
    Write-SswStep '교체된 설치 폴더의 전체 Viewer 패키지 재검증'
    $installedPackage = Get-SswValidatedViewerPackage -Directory $install `
        -ManifestPath (Join-Path $install 'BUILD-MANIFEST.json')
    if ($installedPackage.ManifestSha256 -ne $stagedPackage.ManifestSha256) {
        throw 'VIEWER_INSTALLED_PACKAGE_INVALID: 설치된 BUILD-MANIFEST.json이 검증된 staging과 일치하지 않습니다.'
    }

    $failureCode = 'VIEWER_SMOKE_CHECK_FAILED'
    Write-SswStep '새 Viewer 설치 전용 자체 점검'
    try {
        Invoke-SswViewerSelfCheck -ViewerExecutable $viewerExe -WorkingDirectory $install
    }
    catch {
        if ($_.Exception.Message -match '^([A-Z][A-Z0-9_]{1,63}):') {
            $failureDetailCode = $Matches[1]
        }
        if ($_.Exception.Data.Contains('ExitCode')) {
            $failureExitCode = [int]$_.Exception.Data['ExitCode']
        }
        throw
    }
    $failureDetailCode = $null
    $failureExitCode = $null
    Write-SswStep 'Viewer 설치 전용 자체 점검을 통과했습니다.'

    if ($MachinePhase) {
        $failureCode = 'VIEWER_ROLLBACK_TRANSACTION_WRITE_FAILED'
        $rollbackManifestSha256 = $null
        if (Test-Path -LiteralPath $backup) {
            $rollbackMarkerPackage = Get-SswValidatedViewerRollbackPackage `
                -RollbackSlot $backup
            $rollbackManifestSha256 = $rollbackMarkerPackage.ManifestSha256
        }
        Write-SswViewerRollbackTransaction -InstallDirectory $install `
            -TransactionId $InstallTransactionId `
            -ActiveManifestSha256 $installedPackage.ManifestSha256 `
            -RollbackManifestSha256 $rollbackManifestSha256
    }

    if (-not $MachinePhase) {
        $failureCode = 'VIEWER_SHORTCUT_SETUP_FAILED'
        $shortcutMutationStarted = $true
        Invoke-SswViewerUserIntegration -ViewerExecutable $viewerExe `
            -EnableStartup:$StartWithWindows -DisableStartup:$DisableStartWithWindows
    }

    # Machine rollback slot은 원래 사용자 검증 뒤에도 다음 업데이트까지 보존합니다.
    # PerUser 임시 백업만 durable commit 뒤 정리합니다.
    $failureCode = 'VIEWER_INSTALL_COMMIT_FAILED'
    Write-SswOperationJournal -Path $journalPath -Operation 'viewer-install' -TransactionId $transactionId `
        -Stage 'completed' -Status 'succeeded' -Version ([string]$sourceManifest.version)
    $transactionCommitted = $true
    $cleanupErrors = @(Invoke-SswBestEffortPlan -Plan @(
        [pscustomobject]@{ Name = 'cleanup-program-backup'; Action = {
            if (-not $MachinePhase -and (Test-Path -LiteralPath $backup)) {
                Remove-Item -LiteralPath $backup -Recurse -Force
            }
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
    if (-not $MachinePhase -and -not $DoNotStart) {
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
    if ([string]::IsNullOrWhiteSpace([string]$failureDetailCode) -and
        $failure.Exception.Message -match '^(VIEWER_[A-Z0-9_]{2,63}):') {
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
            if ($previousInstallMovedToBackup -and (Test-Path -LiteralPath $backup)) {
                Move-Item -LiteralPath $backup -Destination $install
            }
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
        $recovery = if ($previousInstallMovedToBackup) {
            'PREVIOUS_VIEWER_RESTORED'
        }
        elseif ($previousInstallExisted -and (Test-Path -LiteralPath $install -PathType Container)) {
            'CURRENT_VIEWER_PRESERVED'
        }
        else {
            'PARTIAL_INSTALL_REMOVED'
        }
        Write-Host "Recovery: $recovery" -ForegroundColor Yellow
        if ($recovery -ceq 'PREVIOUS_VIEWER_RESTORED') {
            Write-Warning '이전 Viewer 파일과 바로 가기를 복구했습니다. Viewer는 실행 중이 아니므로 시작 메뉴에서 다시 실행하세요.'
        }
        elseif ($recovery -ceq 'CURRENT_VIEWER_PRESERVED') {
            Write-Warning '새 Viewer로 교체하기 전에 실패하여 현재 설치는 그대로 유지했습니다.'
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
