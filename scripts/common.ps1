Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SswDotNet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
            return $candidate
        }
    }

    throw '.NET 10 SDK를 찾지 못했습니다. https://dotnet.microsoft.com/download/dotnet/10.0 에서 x64 SDK를 설치하세요.'
}

function Assert-SswAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '관리자 권한 PowerShell에서 실행해야 합니다.'
    }
}

function Test-SswAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-SswChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $childFull = [IO.Path]::GetFullPath($Child).TrimEnd('\')
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        -not $childFull.StartsWith(($parentFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "안전 범위를 벗어난 경로입니다: $childFull"
    }
    Assert-SswNoReparsePoint -Parent $parentFull -Child $childFull
}

function Assert-SswNoReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $childFull = [IO.Path]::GetFullPath($Child).TrimEnd('\')
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase)) { return }
    if (-not $childFull.StartsWith(($parentFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "재분석 지점 검사 범위를 벗어난 경로입니다: $childFull"
    }
    $relative = $childFull.Substring($parentFull.Length + 1)
    $current = $parentFull
    foreach ($segment in $relative.Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "junction 또는 symlink 경로는 자동 변경하지 않습니다: $current"
            }
        }
    }
}

function Assert-SswProductPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BaseRoot,
        [Parameter(Mandatory = $true)][string]$ProductRelativeRoot,
        [switch]$RequireExactProductRoot
    )

    $baseFull = [IO.Path]::GetFullPath($BaseRoot).TrimEnd('\')
    $productFull = [IO.Path]::GetFullPath((Join-Path $baseFull $ProductRelativeRoot)).TrimEnd('\')
    $targetFull = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $isExact = $targetFull.Equals($productFull, [StringComparison]::OrdinalIgnoreCase)
    if (($RequireExactProductRoot -and -not $isExact) -or
        (-not $RequireExactProductRoot -and -not $isExact -and
            -not $targetFull.StartsWith(($productFull + '\'), [StringComparison]::OrdinalIgnoreCase))) {
        throw "SamsungSwitchWatch 전용 안전 경로 밖입니다: $targetFull"
    }
    Assert-SswNoReparsePoint -Parent $baseFull -Child $targetFull
}

function Write-SswStep {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[Samsung Switch Watch] $Message" -ForegroundColor Cyan
}

function New-SswDirectoryIfMissing {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[A-Z][A-Z0-9_]{2,63}$')]
        [string]$FailureCode,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $fullPath) {
        if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            throw ('{0}: {1} 경로가 폴더가 아닙니다.' -f $FailureCode, $Description)
        }
        return $false
    }

    try {
        New-Item -ItemType Directory -Path $fullPath -Force -ErrorAction Stop | Out-Null
    }
    catch {
        $message = '{0}: {1} 폴더를 만들 수 없습니다. 같은 Windows 사용자로 실행하고 쓰기 권한을 확인하세요.' -f
            $FailureCode, $Description
        throw [InvalidOperationException]::new($message, $_.Exception)
    }
    return $true
}

function Remove-SswEmptyDirectoryBestEffort {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
        if (Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1) { return }
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        Write-Warning '설치기가 새로 만든 빈 바로 가기 폴더를 정리하지 못했습니다.'
    }
}

function Get-SswAgentServiceName {
    return 'SamsungSwitchWatchAgent'
}

function Get-SswAgentBackgroundTaskName {
    return 'SamsungSwitchWatchAgent-CurrentUser'
}

function Get-SswCurrentUserSid {
    return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

function Get-SswDeploymentMutexName {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Agent', 'Viewer')]
        [string]$Product
    )

    # v1은 앱 릴리스 번호가 아니라 구·신 설치기가 함께 써야 하는 영구 잠금 프로토콜 식별자입니다.
    if ($Product -eq 'Agent') {
        return 'Global\SamsungSwitchWatch.Agent.Deployment.v1'
    }
    return "Global\SamsungSwitchWatch.Viewer.Deployment.$(Get-SswCurrentUserSid).v1"
}

function New-SswDeploymentMutexSecurity {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Agent', 'Viewer', 'Test')]
        [string]$Product
    )

    $systemSid = 'S-1-5-18'
    $allowedSids = if ($Product -eq 'Agent') {
        @($systemSid, 'S-1-5-32-544')
    }
    else {
        @($systemSid, (Get-SswCurrentUserSid))
    }
    $security = New-Object Security.AccessControl.MutexSecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in $allowedSids) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
        $security.AddAccessRule((New-Object Security.AccessControl.MutexAccessRule(
            $sid,
            [Security.AccessControl.MutexRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)))
    }
    return $security
}

function Enter-SswNamedDeploymentLock {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Product
    )

    if ($Name -notmatch '^(?:Global|Local)\\SamsungSwitchWatch\.[A-Za-z0-9.-]{1,180}$' -or
        $Product -notin @('Agent', 'Viewer', 'Test')) {
        throw 'DEPLOYMENT_LOCK_INVALID: 배포 잠금 이름이 안전한 제품 범위를 벗어났습니다.'
    }
    if ($Product -ne 'Test') {
        $expectedName = Get-SswDeploymentMutexName -Product $Product
        if (-not $Name.Equals($expectedName, [StringComparison]::Ordinal)) {
            throw 'DEPLOYMENT_LOCK_INVALID: 제품과 배포 잠금 이름이 일치하지 않습니다.'
        }
    }

    $mutex = $null
    $acquired = $false
    try {
        try {
            $createdNew = $false
            $security = New-SswDeploymentMutexSecurity -Product $Product
            $mutex = [Threading.Mutex]::new($false, $Name, [ref]$createdNew, $security)
        }
        catch {
            throw 'DEPLOYMENT_LOCK_UNAVAILABLE: 설치·제거 잠금을 만들거나 열 수 없습니다.'
        }

        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
            throw 'DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED: 이전 설치·제거 작업의 비정상 종료를 감지해 자동 변경을 중단했습니다.'
        }
        catch {
            throw 'DEPLOYMENT_LOCK_UNAVAILABLE: 설치·제거 잠금 상태를 확인할 수 없습니다.'
        }

        if (-not $acquired) {
            throw "DEPLOYMENT_ALREADY_RUNNING: $Product 설치 또는 제거 작업이 이미 실행 중입니다."
        }

        return [pscustomobject]@{
            Name = $Name
            Product = $Product
            Mutex = $mutex
        }
    }
    catch {
        if ($mutex) {
            if ($acquired) {
                try { $mutex.ReleaseMutex() } catch { }
            }
            try { $mutex.Dispose() } catch { }
        }
        throw
    }
}

function Enter-SswDeploymentLock {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Agent', 'Viewer')]
        [string]$Product
    )

    $name = Get-SswDeploymentMutexName -Product $Product
    return Enter-SswNamedDeploymentLock -Name $name -Product $Product
}

function Exit-SswDeploymentLock {
    param(
        [AllowNull()][object]$Lock
    )

    if ($null -eq $Lock) { return }
    $mutex = $null
    try {
        $mutex = $Lock.Mutex
        if ($mutex) { $mutex.ReleaseMutex() }
    }
    catch {
        Write-Warning 'DEPLOYMENT_LOCK_RELEASE_FAILED: 설치·제거 잠금을 정상 해제하지 못했습니다.' `
            -WarningAction Continue
    }
    finally {
        if ($mutex) {
            try { $mutex.Dispose() }
            catch {
                Write-Warning 'DEPLOYMENT_LOCK_DISPOSE_FAILED: 설치·제거 잠금 핸들을 정리하지 못했습니다.' `
                    -WarningAction Continue
            }
        }
    }
}

function ConvertTo-SswIdentitySid {
    param([Parameter(Mandatory = $true)][string]$Identity)

    try {
        if ($Identity -match '^S-1-') {
            return (New-Object Security.Principal.SecurityIdentifier($Identity)).Value
        }
        return (New-Object Security.Principal.NTAccount($Identity)).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw "Windows 사용자 SID를 확인하지 못했습니다: $Identity"
    }
}

function Test-SswTrustedAdministrativeOwnerSid {
    param([Parameter(Mandatory = $true)][string]$Sid)

    $normalizedSid = ConvertTo-SswIdentitySid -Identity $Sid
    if ($normalizedSid -in @('S-1-5-18', 'S-1-5-32-544')) { return $true }

    $currentIdentity = $null
    try {
        $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if ($currentIdentity.User -and $currentIdentity.User.Value -eq $normalizedSid) {
            $currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
            if ($currentPrincipal.IsInRole(
                    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
                return $true
            }
        }
    }
    finally {
        if ($currentIdentity -is [IDisposable]) { $currentIdentity.Dispose() }
    }
    # Do not expand local or domain groups for a different account. Directory
    # lookups have no dependable timeout in this Windows PowerShell path and can
    # stall an offline company PC. A different owner therefore requires explicit
    # administrator review instead of inferred trust.
    return $false
}

function Get-SswAclOwnerSid {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.AccessControl.FileSystemSecurity]$Acl
    )

    try {
        return $Acl.GetOwner(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw 'Windows ACL owner SID를 직접 확인하지 못했습니다.'
    }
}

function Get-SswFileSystemAccessRulesBySid {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.AccessControl.FileSystemSecurity]$Acl
    )

    return @($Acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
}

function Clear-SswFileSystemAccessRules {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.AccessControl.FileSystemSecurity]$Acl
    )

    $identitySids = @(
        Get-SswFileSystemAccessRulesBySid -Acl $Acl |
            ForEach-Object { $_.IdentityReference.Value } |
            Select-Object -Unique
    )
    foreach ($identitySid in $identitySids) {
        $Acl.PurgeAccessRules(
            (New-Object Security.Principal.SecurityIdentifier($identitySid)))
    }
}

function Test-SswAdministratorsOnlyFileAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    try {
        $resolved = [IO.Path]::GetFullPath($Path)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { return $false }
        $item = Get-Item -LiteralPath $resolved -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }

        $acl = Get-Acl -LiteralPath $resolved
        if ((Get-SswAclOwnerSid -Acl $acl) -ne 'S-1-5-32-544' -or
            -not $acl.AreAccessRulesProtected) {
            return $false
        }
        $rules = @(Get-SswFileSystemAccessRulesBySid -Acl $acl)
        $requiredSids = @('S-1-5-18', 'S-1-5-32-544')
        if ($rules.Count -ne $requiredSids.Count) { return $false }
        foreach ($requiredSid in $requiredSids) {
            $matches = @($rules | Where-Object {
                $_.IdentityReference.Value -eq $requiredSid -and
                -not $_.IsInherited -and
                $_.AccessControlType -eq
                    [Security.AccessControl.AccessControlType]::Allow -and
                $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and
                $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None -and
                $_.FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl
            })
            if ($matches.Count -ne 1) { return $false }
        }
        return $true
    }
    catch {
        return $false
    }
}

function Assert-SswAdministratorsOnlyFileAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-SswAdministratorsOnlyFileAcl -Path $Path)) {
        throw 'AGENT_RECEIPT_TRUST_INVALID: Agent 설치 영수증이 Administrators 전용 일반 파일이 아니므로 신뢰하지 않습니다.'
    }
}

function Set-SswAdministratorsOnlyFileAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "관리자 전용 ACL을 설정할 파일이 없습니다: $resolved"
    }
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'AGENT_RECEIPT_TRUST_INVALID: junction 또는 symlink 설치 영수증은 사용하지 않습니다.'
    }

    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $acl = Get-Acl -LiteralPath $resolved
    $acl.SetOwner($administratorsSid)
    $acl.SetAccessRuleProtection($true, $false)
    Clear-SswFileSystemAccessRules -Acl $acl
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $resolved -AclObject $acl
    Assert-SswAdministratorsOnlyFileAcl -Path $resolved
}

function Assert-SswLegacyBackgroundRollbackReadyForDataRestore {
    param(
        [AllowNull()][string]$ArchivePath,
        [bool]$ProgramMoveAttempted = $false,
        [bool]$ProgramWasMoved,
        [AllowNull()][string]$ProgramRestorePath,
        [bool]$DataMoveAttempted = $false,
        [bool]$DataWasMoved,
        [AllowNull()][string]$DataRestorePath
    )

    if ([string]::IsNullOrWhiteSpace($ArchivePath)) { return }
    if (($ProgramMoveAttempted -and -not $ProgramWasMoved) -or
        ($DataMoveAttempted -and -not $DataWasMoved)) {
        throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 이전 현재 사용자 Agent 폴더 이동이 부분적으로만 완료되어 활성 Agent data를 보존했습니다.'
    }
    foreach ($item in @(
        [pscustomobject]@{
            WasMoved = $ProgramWasMoved
            Archive = Join-Path $ArchivePath 'program'
            Restored = $ProgramRestorePath
            Label = 'program'
        },
        [pscustomobject]@{
            WasMoved = $DataWasMoved
            Archive = Join-Path $ArchivePath 'data'
            Restored = $DataRestorePath
            Label = 'data'
        }
    )) {
        if (-not $item.WasMoved) { continue }
        if ((Test-Path -LiteralPath $item.Archive) -or
            [string]::IsNullOrWhiteSpace([string]$item.Restored) -or
            -not (Test-Path -LiteralPath ([string]$item.Restored) -PathType Container)) {
            throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 이전 현재 사용자 Agent $($item.Label) 복구가 끝나지 않아 활성 Agent data를 보존했습니다."
        }
    }
}

function Get-SswProgramRollbackDisposition {
    param(
        [Parameter(Mandatory = $true)][bool]$IsUpdate,
        [Parameter(Mandatory = $true)][bool]$InstallSwapped,
        [Parameter(Mandatory = $true)][bool]$ProgramBackupTaken,
        [Parameter(Mandatory = $true)][bool]$InstallExists,
        [Parameter(Mandatory = $true)][bool]$ProgramBackupExists
    )

    if ($ProgramBackupTaken) {
        if (-not $ProgramBackupExists) {
            throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 이전 Agent 프로그램 백업이 없어 활성 프로그램을 보존했습니다.'
        }
        return 'RestoreBackup'
    }
    if ($ProgramBackupExists) {
        throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 기록되지 않은 Agent 프로그램 백업이 있어 자동 복구를 중단했습니다.'
    }
    if ($InstallSwapped) {
        if ($IsUpdate) {
            throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 업데이트 프로그램이 교체됐지만 이전 프로그램 백업이 없어 활성 프로그램을 보존했습니다.'
        }
        return 'QuarantineNewInstall'
    }
    if ($IsUpdate -and -not $InstallExists) {
        throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 프로그램 교체 전 실패했지만 이전 Agent 프로그램 폴더를 확인할 수 없습니다.'
    }
    if (-not $IsUpdate -and $InstallExists) {
        throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 신규 설치 프로그램 교체 완료 여부가 불명확해 활성 프로그램을 보존했습니다.'
    }
    return 'AlreadyIntact'
}

function Restore-SswDirectoryWithQuarantine {
    param(
        [Parameter(Mandatory = $true)][string]$ActivePath,
        [Parameter(Mandatory = $true)][string]$BackupPath,
        [Parameter(Mandatory = $true)][string]$QuarantinePath,
        [switch]$BackupRequired
    )

    $active = [IO.Path]::GetFullPath($ActivePath).TrimEnd('\')
    $backup = [IO.Path]::GetFullPath($BackupPath).TrimEnd('\')
    $quarantine = [IO.Path]::GetFullPath($QuarantinePath).TrimEnd('\')
    $distinct = @(@($active, $backup, $quarantine) | Sort-Object -Unique)
    if ($distinct.Count -ne 3) {
        throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 복구 원본, 활성 경로 및 격리 경로가 서로 달라야 합니다.'
    }
    $roots = @(
        @($active, $backup, $quarantine) |
            ForEach-Object { [IO.Path]::GetPathRoot($_) } |
            Sort-Object -Unique
    )
    if ($roots.Count -ne 1) {
        throw 'AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 디렉터리 복구는 동일 볼륨의 원자적 이름 변경만 허용합니다.'
    }
    if ($BackupRequired -and -not (Test-Path -LiteralPath $backup -PathType Container)) {
        throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 복구 원본 폴더가 없어 활성 사본을 보존했습니다: $backup"
    }
    if (Test-Path -LiteralPath $active) {
        if (-not (Test-Path -LiteralPath $active -PathType Container)) {
            throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 활성 복구 대상이 폴더가 아니므로 보존했습니다: $active"
        }
        $activeItem = Get-Item -LiteralPath $active -Force
        if (($activeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 활성 복구 대상이 reparse point이므로 보존했습니다: $active"
        }
    }
    if (Test-Path -LiteralPath $quarantine) {
        throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 격리 경로가 이미 있어 활성 사본을 보존했습니다: $quarantine"
    }

    $activeQuarantined = $false
    if (Test-Path -LiteralPath $active -PathType Container) {
        try {
            Move-Item -LiteralPath $active -Destination $quarantine -ErrorAction Stop
            $activeQuarantined = $true
        }
        catch {
            if (-not (Test-Path -LiteralPath $active) -and
                (Test-Path -LiteralPath $quarantine -PathType Container)) {
                try {
                    Move-Item -LiteralPath $quarantine -Destination $active -ErrorAction Stop
                }
                catch {
                    throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 활성 폴더 격리 중 오류가 발생했고 원위치 복구도 실패했습니다: $active"
                }
            }
            throw
        }
    }

    if ($BackupRequired) {
        try {
            Move-Item -LiteralPath $backup -Destination $active -ErrorAction Stop
        }
        catch {
            if ($activeQuarantined -and
                -not (Test-Path -LiteralPath $active) -and
                (Test-Path -LiteralPath $quarantine -PathType Container)) {
                try {
                    Move-Item -LiteralPath $quarantine -Destination $active -ErrorAction Stop
                    $activeQuarantined = $false
                }
                catch {
                    throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 백업 복구와 활성 폴더 원위치 복구가 모두 실패했습니다: $active"
                }
            }
            throw
        }
    }
    return $activeQuarantined
}

function Assert-SswTrustedDirectoryRootOwner {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "AGENT_DIRECTORY_TRUST_INVALID: 신뢰할 Agent 폴더가 없습니다: $resolved"
    }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: junction 또는 symlink Agent 폴더는 자동으로 채택하지 않습니다.'
    }

    $ownerSid = Get-SswAclOwnerSid -Acl (Get-Acl -LiteralPath $resolved)
    if (-not (Test-SswTrustedAdministrativeOwnerSid -Sid $ownerSid)) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 폴더 소유자가 SYSTEM, Administrators 또는 현재 elevated 관리자가 아니므로 자동 변경을 중단했습니다.'
    }
    return $ownerSid
}

function Test-SswTrustedAgentDescendantOwnerSid {
    param(
        [Parameter(Mandatory = $true)][string]$OwnerSid,
        [Parameter(Mandatory = $true)][string]$ServiceSid,
        [switch]$AllowLegacyLocalServiceOwner
    )

    $normalizedOwnerSid = (
        New-Object Security.Principal.SecurityIdentifier($OwnerSid)).Value
    $normalizedServiceSid = (
        New-Object Security.Principal.SecurityIdentifier($ServiceSid)).Value
    if ($normalizedOwnerSid -eq $normalizedServiceSid -or
        ($AllowLegacyLocalServiceOwner -and $normalizedOwnerSid -eq 'S-1-5-19')) {
        return $true
    }
    return Test-SswTrustedAdministrativeOwnerSid -Sid $normalizedOwnerSid
}

function Assert-SswBackgroundAgentReceipt {
    param(
        [Parameter(Mandatory = $true)][object]$Receipt,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$DataDirectory,
        [Parameter(Mandatory = $true)][string]$OwnerSid
    )

    $expectedInstall = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $expectedData = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    $receiptInstall = [IO.Path]::GetFullPath([string]$Receipt.installDirectory).TrimEnd('\')
    $receiptData = [IO.Path]::GetFullPath([string]$Receipt.dataDirectory).TrimEnd('\')
    $port = 0
    if ($Receipt.product -ne 'SamsungSwitchWatchBackgroundAgent' -or
        [int]$Receipt.receiptVersion -ne 1 -or
        [string]$Receipt.mode -ne 'current-user-scheduled-task' -or
        [string]$Receipt.taskName -ne (Get-SswAgentBackgroundTaskName) -or
        [string]$Receipt.ownerSid -ne $OwnerSid -or
        -not $receiptInstall.Equals($expectedInstall, [StringComparison]::OrdinalIgnoreCase) -or
        -not $receiptData.Equals($expectedData, [StringComparison]::OrdinalIgnoreCase) -or
        -not [int]::TryParse([string]$Receipt.httpPort, [ref]$port) -or $port -lt 1 -or $port -gt 65535 -or
        [string]$Receipt.executableSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw '현재 사용자 Agent 설치 영수증의 제품·사용자·경로 결속을 확인하지 못했습니다.'
    }
    return $port
}

function Wait-SswServiceDeleted {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "서비스 제거가 제한 시간 안에 완료되지 않았습니다: $Name"
}

function Get-SswServiceSid {
    param([Parameter(Mandatory = $true)][string]$Name)

    $output = & sc.exe showsid $Name 2>&1
    if ($LASTEXITCODE -ne 0) { throw "서비스 SID 조회에 실패했습니다: $Name" }
    $match = [regex]::Match(($output -join "`n"), 'S-1-5-80-(?:\d+-){4}\d+')
    if (-not $match.Success) { throw "서비스 SID를 해석하지 못했습니다: $Name" }
    return $match.Value
}

function Set-SswRestrictedDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ServiceSid,
        [Parameter(Mandatory = $true)]
        [ValidateSet('ReadAndExecute', 'Modify')][string]$ServiceRights,
        [switch]$AllowServiceOwnedDescendants,
        [switch]$AllowLegacyLocalServiceOwnedDescendants
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "ACL을 설정할 폴더가 없습니다: $resolved"
    }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: junction 또는 symlink Agent 폴더는 ACL을 자동 변경하지 않습니다.'
    }

    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $agentSid = New-Object Security.Principal.SecurityIdentifier($ServiceSid)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $allowedSids = @($systemSid.Value, $administratorsSid.Value, $agentSid.Value)
    $ownerTrustCache = @{}
    $testTrustedOwner = {
        param(
            [Parameter(Mandatory = $true)][string]$OwnerSid,
            [switch]$AllowServiceOwner,
            [switch]$AllowLegacyLocalServiceOwner
        )

        $cacheKey = '{0}|{1}|{2}' -f
            ([bool]$AllowServiceOwner),
            ([bool]$AllowLegacyLocalServiceOwner),
            $OwnerSid
        if (-not $ownerTrustCache.ContainsKey($cacheKey)) {
            $ownerTrustCache[$cacheKey] = if ($AllowServiceOwner) {
                Test-SswTrustedAgentDescendantOwnerSid `
                    -OwnerSid $OwnerSid -ServiceSid $agentSid.Value `
                    -AllowLegacyLocalServiceOwner:$AllowLegacyLocalServiceOwner
            }
            else {
                Test-SswTrustedAdministrativeOwnerSid -Sid $OwnerSid
            }
        }
        return [bool]$ownerTrustCache[$cacheKey]
    }

    $acl = Get-Acl -LiteralPath $resolved
    $rootOwnerSid = Get-SswAclOwnerSid -Acl $acl
    if (-not (& $testTrustedOwner $rootOwnerSid)) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 루트 폴더 소유자가 SYSTEM, Administrators 또는 현재 elevated 관리자가 아니므로 자동 변경을 중단했습니다.'
    }

    # Reject a static untrusted tree before changing any ACL. The root-first
    # mutation pass below repeats every check so a race cannot bypass the gate.
    $preflightDirectories = New-Object Collections.Generic.Queue[string]
    $preflightDirectories.Enqueue($resolved)
    while ($preflightDirectories.Count -gt 0) {
        $parent = $preflightDirectories.Dequeue()
        foreach ($item in @(Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'AGENT_DIRECTORY_TRUST_INVALID: junction 또는 symlink가 포함된 Agent 폴더 트리는 자동 변경하지 않습니다.'
            }
            $existingOwnerSid = Get-SswAclOwnerSid -Acl (Get-Acl -LiteralPath $item.FullName)
            $ownerTrusted = if ($AllowServiceOwnedDescendants) {
                & $testTrustedOwner $existingOwnerSid -AllowServiceOwner `
                    -AllowLegacyLocalServiceOwner:$AllowLegacyLocalServiceOwnedDescendants
            }
            else {
                & $testTrustedOwner $existingOwnerSid
            }
            if (-not $ownerTrusted) {
                throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 하위 항목 소유자를 신뢰할 수 없어 자동 변경을 중단했습니다.'
            }
            if ($item.PSIsContainer) {
                $preflightDirectories.Enqueue($item.FullName)
            }
        }
    }

    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: junction 또는 symlink Agent 폴더는 ACL을 자동 변경하지 않습니다.'
    }
    $acl = Get-Acl -LiteralPath $resolved
    $rootOwnerSid = Get-SswAclOwnerSid -Acl $acl
    if (-not (& $testTrustedOwner $rootOwnerSid)) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 루트 폴더 소유자가 SYSTEM, Administrators 또는 현재 elevated 관리자가 아니므로 자동 변경을 중단했습니다.'
    }
    $acl.SetOwner($administratorsSid)
    $acl.SetAccessRuleProtection($true, $false)
    Clear-SswFileSystemAccessRules -Acl $acl
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $agentSid, [Security.AccessControl.FileSystemRights]::$ServiceRights, $inheritance, $propagation, $allow)))
    Set-Acl -LiteralPath $resolved -AclObject $acl

    # Lock each parent before enumerating its children. A standard user can no
    # longer insert a new child after that parent has been inspected.
    $pendingDirectories = New-Object Collections.Generic.Queue[string]
    $pendingDirectories.Enqueue($resolved)
    while ($pendingDirectories.Count -gt 0) {
        $parent = $pendingDirectories.Dequeue()
        foreach ($item in @(Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'AGENT_DIRECTORY_TRUST_INVALID: junction 또는 symlink가 포함된 Agent 폴더 트리는 자동 변경하지 않습니다.'
            }

            $childAcl = Get-Acl -LiteralPath $item.FullName
            $childOwnerSid = Get-SswAclOwnerSid -Acl $childAcl
            $ownerTrusted = if ($AllowServiceOwnedDescendants) {
                & $testTrustedOwner $childOwnerSid -AllowServiceOwner `
                    -AllowLegacyLocalServiceOwner:$AllowLegacyLocalServiceOwnedDescendants
            }
            else {
                & $testTrustedOwner $childOwnerSid
            }
            if (-not $ownerTrusted) {
                throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 하위 항목 소유자를 신뢰할 수 없어 자동 변경을 중단했습니다.'
            }
            $childAcl.SetOwner($administratorsSid)
            $childAcl.SetAccessRuleProtection($true, $false)
            Clear-SswFileSystemAccessRules -Acl $childAcl
            $childAcl.SetAccessRuleProtection($false, $false)
            Set-Acl -LiteralPath $item.FullName -AclObject $childAcl

            if ($item.PSIsContainer) {
                $pendingDirectories.Enqueue($item.FullName)
            }
        }
    }

    $verified = Get-Acl -LiteralPath $resolved
    if ((Get-SswAclOwnerSid -Acl $verified) -ne $administratorsSid.Value) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 루트 폴더 소유자를 Administrators로 고정하지 못했습니다.'
    }
    $verifiedRules = @(Get-SswFileSystemAccessRulesBySid -Acl $verified)
    $unexpected = @($verifiedRules | Where-Object {
        $_.IsInherited -or $_.AccessControlType -ne $allow -or
        $_.IdentityReference.Value -notin $allowedSids
    })
    if ($unexpected.Count -gt 0) {
        throw 'AGENT_DIRECTORY_TRUST_INVALID: 허용되지 않은 Agent 루트 폴더 권한이 남아 있습니다.'
    }
    foreach ($requiredSid in $allowedSids) {
        if (-not ($verifiedRules | Where-Object {
            $_.IdentityReference.Value -eq $requiredSid
        })) {
            throw "AGENT_DIRECTORY_TRUST_INVALID: 필수 Agent 루트 폴더 권한을 확인하지 못했습니다: $requiredSid"
        }
    }
    $verifiedDescendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    foreach ($item in $verifiedDescendants) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'AGENT_DIRECTORY_TRUST_INVALID: ACL 적용 후 junction 또는 symlink가 발견되었습니다.'
        }
        $childAcl = Get-Acl -LiteralPath $item.FullName
        if ((Get-SswAclOwnerSid -Acl $childAcl) -ne $administratorsSid.Value) {
            throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 하위 항목 소유자를 Administrators로 고정하지 못했습니다.'
        }
        $invalidChildRule = Get-SswFileSystemAccessRulesBySid -Acl $childAcl | Where-Object {
            -not $_.IsInherited -or $_.AccessControlType -ne $allow -or
            $_.IdentityReference.Value -notin $allowedSids
        } | Select-Object -First 1
        if ($invalidChildRule) {
            throw 'AGENT_DIRECTORY_TRUST_INVALID: Agent 하위 항목에 허용되지 않은 명시적 권한이 남아 있습니다.'
        }
    }
}

function Get-SswDirectoryAclSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { return }
    $items = @((Get-Item -LiteralPath $resolved -Force)) +
        @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "ACL 백업에서 junction 또는 symlink를 허용하지 않습니다: $($item.FullName)"
        }
        [pscustomobject]@{
            Path = $item.FullName
            Sddl = (Get-Acl -LiteralPath $item.FullName).Sddl
            IsContainer = [bool]$item.PSIsContainer
        }
    }
}

function Restore-SswDirectoryAclSnapshot {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot
    )

    foreach ($entry in $Snapshot | Sort-Object { $_.Path.Length }) {
        if (-not (Test-Path -LiteralPath $entry.Path)) { continue }
        $acl = if ($entry.IsContainer) {
            New-Object Security.AccessControl.DirectorySecurity
        }
        else {
            New-Object Security.AccessControl.FileSecurity
        }
        $acl.SetSecurityDescriptorSddlForm([string]$entry.Sddl)
        Set-Acl -LiteralPath $entry.Path -AclObject $acl
    }
}

function Test-SswTcpPortAvailable {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [string]$Address = '0.0.0.0'
    )

    $listener = $null
    try {
        $ipAddress = [Net.IPAddress]::Parse($Address)
        $listener = New-Object Net.Sockets.TcpListener($ipAddress, $Port)
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($listener) { $listener.Stop() }
    }
}

function Invoke-SswLocalHealthProbe {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 30,
        [switch]$UseHttps
    )

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    if ($UseHttps) {
        # This probe runs only over loopback. The Viewer validates the persistent
        # Agent identity; the installer only needs to prove that HTTPS is ready.
        $handler.ServerCertificateCustomValidationCallback = {
            param($message, $certificate, $chain, $sslPolicyErrors)
            return $true
        }
    }
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(3)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $scheme = if ($UseHttps) { 'https' } else { 'http' }
    $lastStatus = if ($UseHttps) { 'AGENT_HTTPS_UNREACHABLE' } else { 'AGENT_HTTP_UNREACHABLE' }
    try {
        do {
            $response = $null
            try {
                $response = $client.GetAsync("${scheme}://127.0.0.1:$Port/health/ready").GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) { return 'READY' }
                try {
                    $readinessBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    $lastStatus = if ($readinessBody.code) { [string]$readinessBody.code } else { "AGENT_NOT_READY_$([int]$response.StatusCode)" }
                }
                catch { $lastStatus = "AGENT_NOT_READY_$([int]$response.StatusCode)" }
            }
            catch {
                # 서비스 시작 직후의 연결 거부는 제한 시간 동안 재시도합니다.
            }
            finally { if ($response) { $response.Dispose() } }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    throw "Agent readiness 확인이 ${TimeoutSeconds}초 안에 성공하지 못했습니다. 마지막 상태: $lastStatus"
}

function Invoke-SswLocalLivenessProbe {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 30,
        [switch]$UseHttps
    )

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    if ($UseHttps) {
        $handler.ServerCertificateCustomValidationCallback = {
            param($message, $certificate, $chain, $sslPolicyErrors)
            return $true
        }
    }
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(3)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $scheme = if ($UseHttps) { 'https' } else { 'http' }
    try {
        do {
            $response = $null
            try {
                $response = $client.GetAsync("${scheme}://127.0.0.1:$Port/health/live").GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) { return 'LIVE' }
            }
            catch {
                # 예약 작업 시작 직후의 연결 거부는 제한 시간 동안 재시도합니다.
            }
            finally { if ($response) { $response.Dispose() } }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $unreachableCode = if ($UseHttps) { 'AGENT_HTTPS_UNREACHABLE' } else { 'AGENT_HTTP_UNREACHABLE' }
    throw "Agent liveness 확인이 ${TimeoutSeconds}초 안에 성공하지 못했습니다. 진단 코드: $unreachableCode"
}

function Set-SswInstallerBackupAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$ValidateExistingOwner
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "백업 폴더가 없습니다: $resolved" }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "junction 또는 symlink 백업 폴더는 사용하지 않습니다: $resolved"
    }

    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $allowedSids = @($systemSid.Value, $administratorsSid.Value)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $ownerTrustCache = @{}
    $testExistingOwner = {
        param([Parameter(Mandatory = $true)][string]$OwnerSid)
        if (-not $ownerTrustCache.ContainsKey($ownerSid)) {
            $ownerTrustCache[$ownerSid] = Test-SswTrustedAdministrativeOwnerSid -Sid $ownerSid
        }
        return [bool]$ownerTrustCache[$ownerSid]
    }

    $acl = Get-Acl -LiteralPath $resolved
    if ($ValidateExistingOwner -and
        -not (& $testExistingOwner (Get-SswAclOwnerSid -Acl $acl))) {
        throw "백업 폴더 소유자가 로컬 Administrators 구성원이 아닙니다: $resolved"
    }
    if ($ValidateExistingOwner) {
        # Validate the complete existing tree before changing the root so a
        # rejected child cannot leave a partially migrated operations ACL.
        $preflightDirectories = New-Object Collections.Generic.Queue[string]
        $preflightDirectories.Enqueue($resolved)
        while ($preflightDirectories.Count -gt 0) {
            $parent = $preflightDirectories.Dequeue()
            foreach ($item in @(Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop)) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "junction 또는 symlink가 포함된 백업 트리는 사용하지 않습니다: $($item.FullName)"
                }
                $existingOwnerSid = Get-SswAclOwnerSid -Acl (
                    Get-Acl -LiteralPath $item.FullName)
                if (-not (& $testExistingOwner $existingOwnerSid)) {
                    throw "하위 백업 항목 소유자가 로컬 Administrators 구성원이 아닙니다: $($item.FullName)"
                }
                if ($item.PSIsContainer) {
                    $preflightDirectories.Enqueue($item.FullName)
                }
            }
        }
    }
    $acl.SetOwner($administratorsSid)
    $acl.SetAccessRuleProtection($true, $false)
    Clear-SswFileSystemAccessRules -Acl $acl
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    }
    Set-Acl -LiteralPath $resolved -AclObject $acl

    # Parents are secured before their children are enumerated. An unprivileged
    # process therefore cannot add a new child after that parent has been inspected.
    $pendingDirectories = New-Object Collections.Generic.Queue[string]
    $pendingDirectories.Enqueue($resolved)
    while ($pendingDirectories.Count -gt 0) {
        $parent = $pendingDirectories.Dequeue()
        foreach ($item in @(Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "junction 또는 symlink가 포함된 백업 트리는 사용하지 않습니다: $($item.FullName)"
            }

            $childAcl = Get-Acl -LiteralPath $item.FullName
            if ($ValidateExistingOwner -and
                -not (& $testExistingOwner (Get-SswAclOwnerSid -Acl $childAcl))) {
                throw "하위 백업 항목 소유자가 로컬 Administrators 구성원이 아닙니다: $($item.FullName)"
            }
            $childAcl.SetOwner($administratorsSid)
            $childAcl.SetAccessRuleProtection($true, $false)
            Clear-SswFileSystemAccessRules -Acl $childAcl
            $childAcl.SetAccessRuleProtection($false, $false)
            Set-Acl -LiteralPath $item.FullName -AclObject $childAcl

            if ($item.PSIsContainer) {
                $pendingDirectories.Enqueue($item.FullName)
            }
        }
    }

    $verified = Get-Acl -LiteralPath $resolved
    if ((Get-SswAclOwnerSid -Acl $verified) -ne $administratorsSid.Value) {
        throw "백업 폴더 소유자가 로컬 Administrators가 아닙니다: $resolved"
    }
    $verifiedRules = @(Get-SswFileSystemAccessRulesBySid -Acl $verified)
    $unexpected = @($verifiedRules | Where-Object {
        $_.IsInherited -or $_.AccessControlType -ne $allow -or
        $_.IdentityReference.Value -notin $allowedSids
    })
    if ($unexpected.Count -gt 0) {
        throw "허용되지 않은 백업 폴더 권한이 남아 있습니다: $resolved"
    }
    foreach ($requiredSid in $allowedSids) {
        if (-not ($verifiedRules | Where-Object {
            $_.IdentityReference.Value -eq $requiredSid
        })) {
            throw "필수 백업 폴더 권한을 확인하지 못했습니다: $requiredSid"
        }
    }
    $verifiedDescendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    foreach ($item in $verifiedDescendants) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "ACL 적용 후 junction 또는 symlink가 발견되었습니다: $($item.FullName)"
        }
        $childAcl = Get-Acl -LiteralPath $item.FullName
        if ((Get-SswAclOwnerSid -Acl $childAcl) -ne $administratorsSid.Value) {
            throw "하위 백업 항목 소유자가 로컬 Administrators가 아닙니다: $($item.FullName)"
        }
        $invalidChildRule = Get-SswFileSystemAccessRulesBySid -Acl $childAcl | Where-Object {
            -not $_.IsInherited -or $_.AccessControlType -ne $allow -or
            $_.IdentityReference.Value -notin $allowedSids
        } | Select-Object -First 1
        if ($invalidChildRule) {
            throw "하위 백업 항목에 허용되지 않은 명시적 권한이 남아 있습니다: $($item.FullName)"
        }
    }
}

function Initialize-SswAgentOperationsRoot {
    param(
        [Parameter(Mandatory = $true)][string]$OperationsRoot
    )

    $root = [IO.Path]::GetFullPath($OperationsRoot)
    try {
        if (Test-Path -LiteralPath $root) {
            if (-not (Test-Path -LiteralPath $root -PathType Container)) {
                throw 'The Agent operations root is not a directory.'
            }
            Assert-SswNoReparsePoint -Parent (Split-Path $root -Parent) -Child $root
        }
        else {
            New-Item -ItemType Directory -Path $root | Out-Null
        }

        Set-SswInstallerBackupAcl -Path $root -ValidateExistingOwner

        # Inventory is checked only after the parent-first ACL migration has
        # finished, so a concurrent standard user cannot insert a trusted-looking
        # journal between validation and normalization.
        $topLevelItems = @(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop)
        foreach ($item in $topLevelItems) {
            $isExpectedJournal = -not $item.PSIsContainer -and
                $item.Name -in @('agent-install-or-update.json', 'agent-uninstall.json')
            $isExpectedJournalArtifact = -not $item.PSIsContainer -and
                $item.Name -match '^agent-(?:install-or-update|uninstall)\.json\.[0-9a-f]{32}\.(?:tmp|bak)$'
            $isTransactionsDirectory = $item.PSIsContainer -and $item.Name -ceq 'transactions'
            if (-not ($isExpectedJournal -or $isExpectedJournalArtifact -or
                    $isTransactionsDirectory)) {
                throw "Unexpected Agent operations artifact: $($item.Name)"
            }
        }

        $transactionsRoot = Join-Path $root 'transactions'
        if (Test-Path -LiteralPath $transactionsRoot) {
            if (-not (Test-Path -LiteralPath $transactionsRoot -PathType Container)) {
                throw 'The Agent transactions path is not a directory.'
            }
            foreach ($transaction in @(Get-ChildItem -LiteralPath $transactionsRoot -Force)) {
                if (-not $transaction.PSIsContainer -or
                    $transaction.Name -notmatch '^[0-9a-f]{32}$') {
                    throw "Unexpected Agent transaction artifact: $($transaction.Name)"
                }
            }
        }
    }
    catch {
        throw 'AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID: Agent 작업 기록 폴더의 소유권, ACL 또는 파일 구성을 신뢰할 수 없어 자동 변경을 중단했습니다. 폴더와 백업을 삭제하지 말고 관리자에게 확인을 요청하세요.'
    }
}

function Remove-SswOperationJournalArtifactBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        Write-Warning ("작업 journal 임시 파일을 정리하지 못했습니다: {0}" -f $Path) -WarningAction Continue
    }
}

function Write-SswOperationJournal {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Status,
        [string]$Version,
        [string[]]$ErrorCodes = @()
    )

    $journalPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path $journalPath -Parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $payload = [ordered]@{
        formatVersion = 1
        product = 'SamsungSwitchWatch'
        operation = $Operation
        transactionId = $TransactionId
        stage = $Stage
        status = $Status
        version = $Version
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        errorCodes = @($ErrorCodes)
    } | ConvertTo-Json -Depth 5
    $temporary = "$journalPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $replaceBackup = "$journalPath.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [IO.File]::WriteAllText($temporary, $payload, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            [IO.File]::Replace($temporary, $journalPath, $replaceBackup, $true)
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $journalPath
        }
    }
    finally {
        # journal 교체가 이미 성공한 뒤의 임시 백업 정리 실패는 완료된 작업을 실패로 바꾸지 않습니다.
        Remove-SswOperationJournalArtifactBestEffort -Path $temporary
        Remove-SswOperationJournalArtifactBestEffort -Path $replaceBackup
    }
}

function Read-SswAgentDeploymentJournal {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet('agent-install-or-update', 'agent-uninstall')]
        [string]$ExpectedOperation
    )

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록 경로가 일반 파일이 아닙니다. 작업 기록과 백업을 삭제하지 말고 관리자에게 확인을 요청하세요.'
    }

    $journalItem = Get-Item -LiteralPath $Path -Force
    if ($journalItem.Length -gt 65536) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록이 허용 크기 64KiB를 초과했습니다. 자동 변경을 중단했습니다.'
    }

    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open(
            [IO.Path]::GetFullPath($Path),
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        if ($stream.Length -gt 65536) {
            throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록이 허용 크기 64KiB를 초과했습니다. 자동 변경을 중단했습니다.'
        }
        $reader = New-Object IO.StreamReader(
            $stream,
            (New-Object Text.UTF8Encoding($false, $true)),
            $true)
        $journalText = $reader.ReadToEnd()
        $journal = $journalText | ConvertFrom-Json
    }
    catch {
        if ($_.Exception.Message -like 'AGENT_DEPLOYMENT_JOURNAL_INVALID:*') { throw }
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록을 읽을 수 없거나 JSON 형식이 손상되었습니다. 작업 기록과 백업을 삭제하지 말고 관리자에게 확인을 요청하세요.'
    }
    finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
    }
    if ($null -eq $journal -or $journal -is [Array]) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록의 최상위 형식이 올바르지 않습니다. 작업 기록과 백업을 삭제하지 말고 관리자에게 확인을 요청하세요.'
    }

    $requiredProperties = @(
        'formatVersion',
        'product',
        'operation',
        'transactionId',
        'stage',
        'status',
        'updatedUtc',
        'errorCodes'
    )
    $availableProperties = @($journal.PSObject.Properties.Name)
    foreach ($requiredProperty in $requiredProperties) {
        if ($requiredProperty -notin $availableProperties) {
            throw "AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록에 필수 항목이 없습니다: $requiredProperty. 작업 기록과 백업을 삭제하지 말고 관리자에게 확인을 요청하세요."
        }
    }

    $formatVersion = 0
    if (-not [int]::TryParse(
        [string]$journal.formatVersion,
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$formatVersion) -or $formatVersion -ne 1) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: 지원하지 않는 Agent 작업 기록 버전입니다. 이전 설치 상태를 자동으로 추측하지 말고 관리자에게 확인을 요청하세요.'
    }
    if ([string]$journal.product -cne 'SamsungSwitchWatch' -or
        [string]$journal.operation -cne $ExpectedOperation) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록의 제품 또는 작업 종류가 예상값과 다릅니다. 자동 변경을 중단했습니다.'
    }
    if ([string]$journal.transactionId -notmatch '^[0-9a-f]{32}$') {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록의 transaction ID가 올바르지 않습니다. 자동 변경을 중단했습니다.'
    }

    $updatedUtc = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        [string]$journal.updatedUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$updatedUtc)) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록 시간이 올바르지 않습니다. 자동 변경을 중단했습니다.'
    }

    if ($null -eq $journal.errorCodes -or $journal.errorCodes -isnot [Array]) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록의 오류 코드 목록이 배열이 아닙니다. 자동 변경을 중단했습니다.'
    }
    $errorCodes = @($journal.errorCodes)
    foreach ($errorCode in $errorCodes) {
        if ($null -eq $errorCode -or [string]$errorCode -notmatch '^[A-Z0-9_]{1,64}$') {
            throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록의 오류 코드 형식이 올바르지 않습니다. 자동 변경을 중단했습니다.'
        }
    }

    $stage = [string]$journal.stage
    $status = [string]$journal.status
    $requiresRecovery = $false
    if ($ExpectedOperation -eq 'agent-install-or-update') {
        if ($stage -eq 'prepared' -and $status -eq 'running' -and $errorCodes.Count -eq 0) {
            $requiresRecovery = $true
        }
        elseif ($stage -eq 'completed' -and $status -eq 'succeeded' -and $errorCodes.Count -eq 0) {
        }
        elseif ($stage -eq 'rollback-completed' -and $status -eq 'failed') {
            $requiresRecovery = $errorCodes.Count -gt 0
        }
        else {
            throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 설치 작업 기록의 단계와 상태 조합이 올바르지 않습니다. 자동 변경을 중단했습니다.'
        }
    }
    else {
        if ($stage -eq 'prepared' -and $status -eq 'running' -and $errorCodes.Count -eq 0) {
            $requiresRecovery = $true
        }
        elseif ($stage -eq 'completed' -and $status -eq 'succeeded' -and $errorCodes.Count -eq 0) {
        }
        elseif ($stage -eq 'completed' -and $status -eq 'failed' -and $errorCodes.Count -gt 0) {
            $requiresRecovery = $true
        }
        else {
            throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 제거 작업 기록의 단계와 상태 조합이 올바르지 않습니다. 자동 변경을 중단했습니다.'
        }
    }

    return [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($Path)
        Operation = $ExpectedOperation
        TransactionId = [string]$journal.transactionId
        Stage = $stage
        Status = $status
        ErrorCodes = $errorCodes
        UpdatedUtc = $updatedUtc
        RequiresRecovery = $requiresRecovery
    }
}

function Assert-SswAgentDeploymentJournalsReady {
    param(
        [Parameter(Mandatory = $true)][string]$OperationsRoot
    )

    $root = [IO.Path]::GetFullPath($OperationsRoot)
    if (-not (Test-Path -LiteralPath $root)) { return }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw 'AGENT_DEPLOYMENT_JOURNAL_INVALID: Agent 작업 기록 폴더 경로가 디렉터리가 아닙니다. 자동 변경을 중단했습니다.'
    }
    Assert-SswNoReparsePoint -Parent (Split-Path $root -Parent) -Child $root

    $journalSpecifications = @(
        [pscustomobject]@{
            FileName = 'agent-install-or-update.json'
            Operation = 'agent-install-or-update'
        },
        [pscustomobject]@{
            FileName = 'agent-uninstall.json'
            Operation = 'agent-uninstall'
        }
    )
    foreach ($specification in $journalSpecifications) {
        $journalPath = Join-Path $root $specification.FileName
        Assert-SswChildPath -Parent $root -Child $journalPath
        $state = Read-SswAgentDeploymentJournal -Path $journalPath `
            -ExpectedOperation $specification.Operation
        if ($state -and $state.RequiresRecovery) {
            throw "AGENT_DEPLOYMENT_RECOVERY_REQUIRED: 이전 Agent 설치 또는 제거 작업이 정상적으로 완료되지 않았습니다 ($($specification.FileName)). 파일, 서비스, 방화벽, 작업 기록 또는 백업을 임의로 삭제하지 말고 관리자 확인 후 다시 실행하세요."
        }
    }
}

function Invoke-SswBestEffortPlan {
    param(
        [Parameter(Mandatory = $true)][object[]]$Plan
    )

    $errors = New-Object Collections.Generic.List[string]
    foreach ($step in $Plan) {
        try {
            & $step.Action
        }
        catch {
            $code = "{0}_FAILED" -f ([string]$step.Name).ToUpperInvariant().Replace('-', '_')
            $errors.Add($code)
            Write-Warning ("복구 단계 실패 [{0}]: {1}" -f $step.Name, $_.Exception.Message) -WarningAction Continue
        }
    }
    return @($errors)
}

function ConvertTo-SswViewerRemoteAddresses {
    param([Parameter(Mandatory = $true)][string[]]$Address)

    if ($Address.Count -lt 1 -or $Address.Count -gt 32) {
        throw 'ViewerRemoteAddress는 1~32개의 고정 IPv4 주소여야 합니다.'
    }
    $normalized = New-Object Collections.Generic.List[string]
    foreach ($candidate in $Address) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or $candidate -match '[/\\]') {
            throw "ViewerRemoteAddress에는 서브넷이 아닌 고정 IPv4 주소만 사용할 수 있습니다: $candidate"
        }
        $trimmed = $candidate.Trim()
        if ($trimmed -notmatch '^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}$') {
            throw "ViewerRemoteAddress는 4개 십진 octet의 canonical dotted-quad 형식이어야 합니다: $candidate"
        }
        $octets = @($trimmed.Split('.') | ForEach-Object { [int]$_ })
        if (@($octets | Where-Object { $_ -gt 255 }).Count -gt 0) {
            throw "ViewerRemoteAddress의 각 octet은 0~255 범위여야 합니다: $candidate"
        }
        $parsed = $null
        if (-not [Net.IPAddress]::TryParse($trimmed, [ref]$parsed) -or
            $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
            throw "ViewerRemoteAddress가 유효한 IPv4 주소가 아닙니다: $candidate"
        }
        $normalized.Add($parsed.ToString())
    }
    return @($normalized | Select-Object -Unique | Sort-Object {
        $bytes = [Net.IPAddress]::Parse($_).GetAddressBytes()
        ([uint64]$bytes[0] -shl 24) -bor ([uint64]$bytes[1] -shl 16) -bor
            ([uint64]$bytes[2] -shl 8) -bor [uint64]$bytes[3]
    })
}

function ConvertTo-SswIpv4Cidrs {
    param(
        [Parameter(Mandatory = $true)][string[]]$Cidr,
        [ValidateRange(1, 64)][int]$MaximumCount = 32
    )

    if ($Cidr.Count -lt 1 -or $Cidr.Count -gt $MaximumCount) {
        throw "CIDR list must contain between 1 and $MaximumCount entries."
    }

    $normalized = New-Object Collections.Generic.List[string]
    foreach ($candidate in $Cidr) {
        $trimmed = ([string]$candidate).Trim()
        if ($trimmed -notmatch '^(?<address>(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3})/(?<prefix>\d{1,2})$') {
            throw "CIDR must use canonical IPv4/prefix notation: $candidate"
        }

        $prefix = [int]$Matches.prefix
        if ($prefix -lt 0 -or $prefix -gt 32) { throw "CIDR prefix must be between 0 and 32: $candidate" }
        $octets = @($Matches.address.Split('.') | ForEach-Object { [int]$_ })
        if (@($octets | Where-Object { $_ -gt 255 }).Count -gt 0) {
            throw "CIDR octets must be between 0 and 255: $candidate"
        }

        [uint32]$value = ([uint32]$octets[0] -shl 24) -bor ([uint32]$octets[1] -shl 16) -bor
            ([uint32]$octets[2] -shl 8) -bor [uint32]$octets[3]
        [uint32]$mask = if ($prefix -eq 0) { 0 } else { [uint32]::MaxValue -shl (32 - $prefix) }
        [uint32]$network = $value -band $mask
        $networkAddress = '{0}.{1}.{2}.{3}' -f
            (($network -shr 24) -band 0xff),
            (($network -shr 16) -band 0xff),
            (($network -shr 8) -band 0xff),
            ($network -band 0xff)
        $normalized.Add("$networkAddress/$prefix")
    }

    return @($normalized | Select-Object -Unique | Sort-Object)
}

function Get-SswSwitchInventoryHash {
    param([Parameter(Mandatory = $true)][object[]]$Switches)

    $canonical = $Switches | ConvertTo-Json -Depth 6 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function ConvertTo-SswFirewallSnapshot {
    param([Parameter(Mandatory = $true)][object]$Rule)

    $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $Rule
    $address = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $Rule
    $application = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $Rule
    $service = Get-NetFirewallServiceFilter -AssociatedNetFirewallRule $Rule
    $interfaceType = Get-NetFirewallInterfaceTypeFilter -AssociatedNetFirewallRule $Rule
    return [pscustomobject]@{
        Name = [string]$Rule.Name
        DisplayName = [string]$Rule.DisplayName
        Group = [string]$Rule.Group
        Description = [string]$Rule.Description
        Enabled = [string]$Rule.Enabled
        Direction = [string]$Rule.Direction
        Action = [string]$Rule.Action
        Profile = [string]$Rule.Profile
        Protocol = [string]$port.Protocol
        LocalPort = [string]$port.LocalPort
        RemotePort = [string]$port.RemotePort
        LocalAddress = @($address.LocalAddress | ForEach-Object { [string]$_ })
        RemoteAddress = @($address.RemoteAddress | ForEach-Object { [string]$_ })
        Program = [string]$application.Program
        Service = [string]$service.Service
        InterfaceType = [string]$interfaceType.InterfaceType
    }
}

function Get-SswAgentFirewallSnapshot {
    param([string]$DisplayName = 'Samsung Switch Watch Agent HTTP')

    $rules = @(Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue)
    if ($rules.Count -eq 0) { return $null }
    if ($rules.Count -ne 1) { throw "Agent 방화벽 규칙이 중복되어 있습니다: $DisplayName" }
    return ConvertTo-SswFirewallSnapshot -Rule $rules[0]
}

function Get-SswAgentFirewallSnapshotByName {
    param([Parameter(Mandatory = $true)][string]$Name)

    $rules = @(Get-NetFirewallRule -Name $Name -ErrorAction SilentlyContinue)
    if ($rules.Count -eq 0) { return $null }
    if ($rules.Count -ne 1) { throw "Agent 방화벽 내부 이름이 중복되어 있습니다: $Name" }
    return ConvertTo-SswFirewallSnapshot -Rule $rules[0]
}

function Test-SswOwnedAgentFirewallRule {
    param([Parameter(Mandatory = $true)][object]$Snapshot)
    return $Snapshot.Name -eq 'SamsungSwitchWatchAgent-Http' -and
        $Snapshot.DisplayName -eq 'Samsung Switch Watch Agent HTTP' -and
        $Snapshot.Group -eq 'Samsung Switch Watch' -and
        $Snapshot.Description -eq 'Owned by SamsungSwitchWatchAgent installer v2'
}

function Test-SswLegacyOwnedAgentFirewallRule {
    param([Parameter(Mandatory = $true)][object]$Snapshot)
    return $Snapshot.Name -eq 'SamsungSwitchWatchAgent-Https' -and
        $Snapshot.DisplayName -eq 'Samsung Switch Watch Agent HTTPS' -and
        $Snapshot.Group -eq 'Samsung Switch Watch' -and
        $Snapshot.Description -eq 'Owned by SamsungSwitchWatchAgent installer v1'
}

function Test-SswOwnedAgentHttpsFirewallRule {
    param([Parameter(Mandatory = $true)][object]$Snapshot)
    return $Snapshot.Name -eq 'SamsungSwitchWatchAgent-Https' -and
        $Snapshot.DisplayName -eq 'Samsung Switch Watch Agent HTTPS' -and
        $Snapshot.Group -eq 'Samsung Switch Watch' -and
        $Snapshot.Description -eq 'Owned by SamsungSwitchWatchAgent installer v3'
}

function Assert-SswAgentFirewallNameSafety {
    foreach ($definition in @(
        [pscustomobject]@{ Name = 'SamsungSwitchWatchAgent-Http'; Kind = 'legacy-http' },
        [pscustomobject]@{ Name = 'SamsungSwitchWatchAgent-Https'; Kind = 'https' }
    )) {
        $snapshot = Get-SswAgentFirewallSnapshotByName -Name $definition.Name
        if (-not $snapshot) { continue }
        $owned = if ($definition.Kind -eq 'legacy-http') {
            Test-SswOwnedAgentFirewallRule -Snapshot $snapshot
        } else {
            (Test-SswOwnedAgentHttpsFirewallRule -Snapshot $snapshot) -or
                (Test-SswLegacyOwnedAgentFirewallRule -Snapshot $snapshot)
        }
        if (-not $owned) {
            throw "제품 내부 이름과 충돌하는 외부 방화벽 규칙이 있습니다. 자동 변경하지 않습니다: $($definition.Name)"
        }
    }
}

function Test-SswFirewallPortOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$Protocol,
        [Parameter(Mandatory = $true)][string[]]$LocalPort,
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$TargetPort
    )

    if ($Protocol -notin @('TCP', '6', 'Any', '256', '*')) { return $false }
    foreach ($entry in $LocalPort) {
        foreach ($token in ([string]$entry).Split(',')) {
            $value = $token.Trim()
            if ($value -in @('Any', '*')) { return $true }
            $singlePort = 0
            if ([int]::TryParse($value, [ref]$singlePort)) {
                if ($singlePort -eq $TargetPort) { return $true }
                continue
            }
            if ($value -match '^(\d{1,5})-(\d{1,5})$') {
                $start = [int]$Matches[1]
                $end = [int]$Matches[2]
                if ($start -le $TargetPort -and $TargetPort -le $end) { return $true }
                continue
            }
            return $true
        }
    }
    return $false
}

function Test-SswFirewallProfileSetExact {
    param([Parameter(Mandatory = $true)][string]$Profile)

    $profiles = @($Profile.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    return $profiles.Count -eq 2 -and $profiles[0] -eq 'Domain' -and $profiles[1] -eq 'Private'
}

function Test-SswFirewallRuleMayApplyToAgent {
    param(
        [Parameter(Mandatory = $true)][object]$Rule,
        [Parameter(Mandatory = $true)][string]$AgentExecutablePath
    )

    try {
        $applicationFilters = @(Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $Rule)
        $programApplies = $applicationFilters.Count -eq 0
        foreach ($filter in $applicationFilters) {
            $program = [Environment]::ExpandEnvironmentVariables([string]$filter.Program)
            if ([string]::IsNullOrWhiteSpace($program) -or $program -in @('Any', '*')) {
                $programApplies = $true
                break
            }
            try {
                if ([IO.Path]::GetFullPath($program).Equals([IO.Path]::GetFullPath($AgentExecutablePath), [StringComparison]::OrdinalIgnoreCase)) {
                    $programApplies = $true
                    break
                }
            }
            catch { return $true }
        }
        if (-not $programApplies) { return $false }

        $serviceFilters = @(Get-NetFirewallServiceFilter -AssociatedNetFirewallRule $Rule)
        if ($serviceFilters.Count -eq 0) { return $true }
        foreach ($filter in $serviceFilters) {
            $service = [string]$filter.Service
            if ([string]::IsNullOrWhiteSpace($service) -or $service -in @('Any', '*', 'SamsungSwitchWatchAgent')) { return $true }
        }
        return $false
    }
    catch { return $true }
}

function Assert-SswAgentFirewallGateReady {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][string]$AgentExecutablePath
    )

    Assert-SswAgentFirewallNameSafety
    $firewallService = Get-Service -Name 'MpsSvc' -ErrorAction Stop
    if ($firewallService.Status -ne 'Running') {
        throw 'Windows Defender Firewall 서비스(MpsSvc)가 실행 중이어야 Agent HTTP를 사용할 수 있습니다.'
    }
    $profiles = @(Get-NetFirewallProfile -Name Domain,Private,Public -ErrorAction Stop)
    foreach ($requiredName in @('Domain', 'Private', 'Public')) {
        $profile = $profiles | Where-Object { [string]$_.Name -eq $requiredName } | Select-Object -First 1
        if (-not $profile -or $profile.Enabled -ne $true) {
            throw "Windows Firewall $requiredName 프로필이 활성화되어야 Agent HTTP를 사용할 수 있습니다."
        }
        if ([string]$profile.DefaultInboundAction -eq 'Allow') {
            throw "Windows Firewall $requiredName 프로필의 기본 인바운드 정책이 Allow이면 Agent HTTP를 사용할 수 없습니다."
        }
        if ([string]$profile.AllowInboundRules -eq 'False' -or
            [string]$profile.AllowLocalFirewallRules -eq 'False') {
            throw "Windows Firewall $requiredName 프로필 정책이 로컬 인바운드 허용 규칙 적용을 차단합니다."
        }
    }

    foreach ($rule in @(Get-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -ErrorAction Stop)) {
        $candidateSnapshot = if ([string]$rule.Name -eq 'SamsungSwitchWatchAgent-Http') {
            Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http'
        }
        elseif ([string]$rule.Name -eq 'SamsungSwitchWatchAgent-Https') {
            Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https'
        }
        else { $null }
        if ($candidateSnapshot -and ((Test-SswOwnedAgentFirewallRule -Snapshot $candidateSnapshot) -or
            (Test-SswLegacyOwnedAgentFirewallRule -Snapshot $candidateSnapshot) -or
            (Test-SswOwnedAgentHttpsFirewallRule -Snapshot $candidateSnapshot))) { continue }

        $portFilter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule
        if (-not (Test-SswFirewallPortOverlap -Protocol ([string]$portFilter.Protocol) `
            -LocalPort @($portFilter.LocalPort | ForEach-Object { [string]$_ }) -TargetPort $Port)) { continue }
        if (-not (Test-SswFirewallRuleMayApplyToAgent -Rule $rule -AgentExecutablePath $AgentExecutablePath)) { continue }
        throw ("제품 소유가 아닌 활성 인바운드 Allow 규칙이 Agent TCP/{0}과 겹칩니다: {1} ({2})" -f
            $Port, [string]$rule.DisplayName, [string]$rule.Name)
    }
}

function Test-SswAgentFirewallRuleExact {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][string[]]$RemoteAddress
    )

    $expected = @(ConvertTo-SswViewerRemoteAddresses -Address $RemoteAddress)
    $actual = @($Snapshot.RemoteAddress | ForEach-Object { [string]$_ } | Sort-Object)
    $expectedSorted = @($expected | Sort-Object)
    return (Test-SswOwnedAgentFirewallRule -Snapshot $Snapshot) -and
        $Snapshot.Enabled -eq 'True' -and $Snapshot.Direction -eq 'Inbound' -and
        $Snapshot.Action -eq 'Allow' -and $Snapshot.Protocol -in @('TCP', '6') -and
        $Snapshot.LocalPort -eq [string]$Port -and $Snapshot.RemotePort -eq 'Any' -and
        (@($Snapshot.LocalAddress) -join '|') -eq 'Any' -and
        $Snapshot.Program -eq 'Any' -and $Snapshot.Service -eq 'Any' -and
        $Snapshot.InterfaceType -eq 'Any' -and
        ($actual -join '|') -eq ($expectedSorted -join '|') -and
        (Test-SswFirewallProfileSetExact -Profile ([string]$Snapshot.Profile))
}

function New-SswAgentFirewallRule {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory = $true)][string[]]$RemoteAddress
    )

    $validatedAddresses = @(ConvertTo-SswViewerRemoteAddresses -Address $RemoteAddress)
    Assert-SswAgentFirewallNameSafety
    if (Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Http') {
        throw 'Agent HTTP 방화벽 내부 이름이 이미 사용 중입니다.'
    }
    New-NetFirewallRule -Name 'SamsungSwitchWatchAgent-Http' `
        -DisplayName 'Samsung Switch Watch Agent HTTP' -Group 'Samsung Switch Watch' `
        -Description 'Owned by SamsungSwitchWatchAgent installer v2' `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
        -RemotePort Any -LocalAddress Any -RemoteAddress $validatedAddresses `
        -Program Any -Service Any -InterfaceType Any -Profile Domain,Private | Out-Null
}

function Test-SswAgentHttpsFirewallRuleExact {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string[]]$RemoteAddress
    )

    $expected = @(ConvertTo-SswIpv4Cidrs -Cidr $RemoteAddress | Sort-Object)
    try {
        $actualInputs = @($Snapshot.RemoteAddress | ForEach-Object {
            $address = ([string]$_).Trim()
            if ($address -match '/') { $address } else { "$address/32" }
        })
        $actual = @(ConvertTo-SswIpv4Cidrs -Cidr $actualInputs | Sort-Object)
    }
    catch {
        return $false
    }
    return (Test-SswOwnedAgentHttpsFirewallRule -Snapshot $Snapshot) -and
        $Snapshot.Enabled -eq 'True' -and $Snapshot.Direction -eq 'Inbound' -and
        $Snapshot.Action -eq 'Allow' -and $Snapshot.Protocol -in @('TCP', '6') -and
        $Snapshot.LocalPort -eq '18443' -and $Snapshot.RemotePort -eq 'Any' -and
        (@($Snapshot.LocalAddress) -join '|') -eq 'Any' -and
        $Snapshot.Program -eq 'Any' -and $Snapshot.Service -eq 'Any' -and
        $Snapshot.InterfaceType -eq 'Any' -and
        ($actual -join '|') -eq ($expected -join '|') -and
        (Test-SswFirewallProfileSetExact -Profile ([string]$Snapshot.Profile))
}

function New-SswAgentHttpsFirewallRule {
    param([Parameter(Mandatory = $true)][string[]]$RemoteAddress)

    $validatedAddresses = @(ConvertTo-SswIpv4Cidrs -Cidr $RemoteAddress)
    Assert-SswAgentFirewallNameSafety
    if (Get-SswAgentFirewallSnapshotByName -Name 'SamsungSwitchWatchAgent-Https') {
        throw 'Agent HTTPS firewall name is already in use.'
    }
    New-NetFirewallRule -Name 'SamsungSwitchWatchAgent-Https' `
        -DisplayName 'Samsung Switch Watch Agent HTTPS' -Group 'Samsung Switch Watch' `
        -Description 'Owned by SamsungSwitchWatchAgent installer v3' `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort 18443 `
        -RemotePort Any -LocalAddress Any -RemoteAddress $validatedAddresses `
        -Program Any -Service Any -InterfaceType Any -Profile Domain,Private | Out-Null
}

function Remove-SswOwnedAgentFirewallRuleByName {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('SamsungSwitchWatchAgent-Http', 'SamsungSwitchWatchAgent-Https')][string]$Name,
        [switch]$AllowMissing
    )

    $snapshot = Get-SswAgentFirewallSnapshotByName -Name $Name
    if (-not $snapshot) {
        if ($AllowMissing) { return }
        throw "Agent 방화벽 규칙을 찾지 못했습니다: $Name"
    }
    $owned = if ($Name -eq 'SamsungSwitchWatchAgent-Https') {
        (Test-SswOwnedAgentHttpsFirewallRule -Snapshot $snapshot) -or
            (Test-SswLegacyOwnedAgentFirewallRule -Snapshot $snapshot)
    }
    else { Test-SswOwnedAgentFirewallRule -Snapshot $snapshot }
    if (-not $owned) { throw "소유권 표식이 없는 방화벽 규칙은 자동 제거하지 않습니다: $Name" }
    Get-NetFirewallRule -Name $Name -ErrorAction Stop | Remove-NetFirewallRule
}

function Restore-SswAgentFirewallSnapshot {
    param([AllowNull()][object]$Snapshot)

    if ($Snapshot -and -not ((Test-SswOwnedAgentFirewallRule -Snapshot $Snapshot) -or
        (Test-SswLegacyOwnedAgentFirewallRule -Snapshot $Snapshot))) {
        throw '제품 소유권이 확인되지 않은 방화벽 snapshot은 복원하지 않습니다.'
    }
    Assert-SswAgentFirewallNameSafety
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' -AllowMissing
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Https' -AllowMissing
    if ($null -eq $Snapshot) { return }
    $parameters = @{
        Name = $Snapshot.Name
        DisplayName = $Snapshot.DisplayName
        Enabled = $Snapshot.Enabled
        Direction = $Snapshot.Direction
        Action = $Snapshot.Action
        Protocol = $Snapshot.Protocol
        LocalPort = $Snapshot.LocalPort
        RemotePort = $Snapshot.RemotePort
        LocalAddress = @($Snapshot.LocalAddress)
        RemoteAddress = @($Snapshot.RemoteAddress)
        Program = $Snapshot.Program
        Service = $Snapshot.Service
        InterfaceType = $Snapshot.InterfaceType
        Profile = $Snapshot.Profile
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Snapshot.Group)) { $parameters.Group = [string]$Snapshot.Group }
    if (-not [string]::IsNullOrWhiteSpace([string]$Snapshot.Description)) { $parameters.Description = [string]$Snapshot.Description }
    New-NetFirewallRule @parameters | Out-Null
}

function Restore-SswAgentFirewallSnapshots {
    param([object[]]$Snapshots = @())

    foreach ($snapshot in @($Snapshots)) {
        if ($snapshot -and -not ((Test-SswOwnedAgentFirewallRule -Snapshot $snapshot) -or
            (Test-SswLegacyOwnedAgentFirewallRule -Snapshot $snapshot) -or
            (Test-SswOwnedAgentHttpsFirewallRule -Snapshot $snapshot))) {
            throw 'Refusing to restore a firewall snapshot without a product ownership marker.'
        }
    }

    Assert-SswAgentFirewallNameSafety
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' -AllowMissing
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Https' -AllowMissing
    foreach ($snapshot in @($Snapshots | Where-Object { $null -ne $_ })) {
        $parameters = @{
            Name = $snapshot.Name
            DisplayName = $snapshot.DisplayName
            Enabled = $snapshot.Enabled
            Direction = $snapshot.Direction
            Action = $snapshot.Action
            Protocol = $snapshot.Protocol
            LocalPort = $snapshot.LocalPort
            RemotePort = $snapshot.RemotePort
            LocalAddress = @($snapshot.LocalAddress)
            RemoteAddress = @($snapshot.RemoteAddress)
            Program = $snapshot.Program
            Service = $snapshot.Service
            InterfaceType = $snapshot.InterfaceType
            Profile = $snapshot.Profile
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$snapshot.Group)) { $parameters.Group = [string]$snapshot.Group }
        if (-not [string]::IsNullOrWhiteSpace([string]$snapshot.Description)) {
            $parameters.Description = [string]$snapshot.Description
        }
        New-NetFirewallRule @parameters | Out-Null
    }
}

function Remove-SswOwnedAgentFirewallRule {
    param([switch]$AllowMissing)
    Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' -AllowMissing:$AllowMissing
}

function Assert-SswAgentInstallReceipt {
    param(
        [Parameter(Mandatory = $true)][object]$Receipt,
        [Parameter(Mandatory = $true)][string]$AgentId,
        [Parameter(Mandatory = $true)][string]$SwitchInventoryHash,
        [Parameter(Mandatory = $true)][ValidateRange(1, 256)][int]$SwitchCount
    )

    $receiptVersion = 0
    if ($Receipt.product -ne 'SamsungSwitchWatchAgent' -or
        -not [int]::TryParse([string]$Receipt.receiptVersion, [ref]$receiptVersion) -or
        $receiptVersion -notin @(1, 2)) {
        throw '지원하지 않거나 제품 소유권을 확인할 수 없는 Agent 설치 영수증입니다.'
    }
    $receiptSwitchCount = 0
    if ([string]$Receipt.agentId -ne $AgentId -or
        -not [int]::TryParse([string]$Receipt.switchCount, [ref]$receiptSwitchCount) -or
        $receiptSwitchCount -ne $SwitchCount -or
        [string]$Receipt.switchInventoryHash -ne $SwitchInventoryHash) {
        throw 'Agent 설치 영수증의 Agent ID 또는 스위치 인벤토리가 현재 설정과 일치하지 않습니다.'
    }
    return $receiptVersion
}

function Assert-SswAgentExecutorReceipt {
    param(
        [Parameter(Mandatory = $true)][object]$Receipt,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$DataDirectory
    )

    $expectedInstall = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $expectedData = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    $receiptInstall = [IO.Path]::GetFullPath([string]$Receipt.installDirectory).TrimEnd('\')
    $receiptData = [IO.Path]::GetFullPath([string]$Receipt.dataDirectory).TrimEnd('\')
    if ($Receipt.product -ne 'SamsungSwitchWatchAgent' -or
        [int]$Receipt.receiptVersion -ne 3 -or
        [int]$Receipt.httpsPort -ne 18443 -or
        -not $receiptInstall.Equals($expectedInstall, [StringComparison]::OrdinalIgnoreCase) -or
        -not $receiptData.Equals($expectedData, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Receipt.agentId -notmatch '^[A-Za-z0-9_-]{1,64}$') {
        throw 'Agent executor install receipt validation failed.'
    }
    $clientCidrs = @(ConvertTo-SswIpv4Cidrs -Cidr @($Receipt.clientManagementCidrs))
    $targetCidrs = @(ConvertTo-SswIpv4Cidrs -Cidr @($Receipt.allowedTargetCidrs))
    return [pscustomobject]@{
        AgentId = [string]$Receipt.agentId
        ClientManagementCidrs = $clientCidrs
        AllowedTargetCidrs = $targetCidrs
    }
}

function Get-SswCertificateSha256 {
    param([Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Certificate.RawData))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-SswLegacyOwnedAgentCertificateThumbprints {
    param(
        [Parameter(Mandatory = $true)][object]$Receipt,
        [Parameter(Mandatory = $true)][object]$Configuration
    )

    $result = New-Object Collections.Generic.List[string]
    $expectedFriendlyName = "Samsung Switch Watch Agent $([string]$Receipt.agentId)"
    $httpsProperty = $Configuration.Agent.PSObject.Properties['Https']
    if (-not $httpsProperty -or -not $httpsProperty.Value) { return @() }

    $activeConfigThumbprint = ([string]$httpsProperty.Value.CertificateStoreThumbprint).Replace(' ', '').ToUpperInvariant()
    $activeReceiptThumbprint = ([string]$Receipt.certificateStoreThumbprint).Replace(' ', '').ToUpperInvariant()
    $activeOwned = $Receipt.PSObject.Properties['certificateOwnedByInstaller'] -and
        $Receipt.certificateOwnedByInstaller -eq $true
    if ($activeOwned -and $activeConfigThumbprint -match '^[0-9A-F]{40}$' -and
        $activeReceiptThumbprint -eq $activeConfigThumbprint) {
        $certificatePath = "Cert:\LocalMachine\My\$activeConfigThumbprint"
        if (Test-Path -LiteralPath $certificatePath) {
            $certificate = Get-Item -LiteralPath $certificatePath
            $receiptSha = ([string]$Receipt.certificateSha256).Replace(' ', '').ToUpperInvariant()
            if ($certificate.FriendlyName -eq $expectedFriendlyName -and
                $receiptSha -match '^[0-9A-F]{64}$' -and
                (Get-SswCertificateSha256 -Certificate $certificate) -eq $receiptSha) {
                $result.Add($activeConfigThumbprint)
            }
        }
    }

    $previousOwned = $Receipt.PSObject.Properties['previousCertificateOwnedByInstaller'] -and
        $Receipt.previousCertificateOwnedByInstaller -eq $true
    $previousThumbprint = ([string]$Receipt.previousCertificateStoreThumbprint).Replace(' ', '').ToUpperInvariant()
    $previousReceiptSha = ([string]$Receipt.previousCertificateSha256).Replace(' ', '').ToUpperInvariant()
    $previousConfigSha = ([string]$httpsProperty.Value.PreviousCertificateSha256Fingerprint).Replace(' ', '').ToUpperInvariant()
    if ($previousOwned -and $previousThumbprint -match '^[0-9A-F]{40}$' -and
        $previousReceiptSha -match '^[0-9A-F]{64}$' -and $previousConfigSha -eq $previousReceiptSha) {
        $certificatePath = "Cert:\LocalMachine\My\$previousThumbprint"
        if (Test-Path -LiteralPath $certificatePath) {
            $certificate = Get-Item -LiteralPath $certificatePath
            if ($certificate.FriendlyName -eq $expectedFriendlyName -and
                (Get-SswCertificateSha256 -Certificate $certificate) -eq $previousReceiptSha) {
                $result.Add($previousThumbprint)
            }
        }
    }
    return @($result | Select-Object -Unique)
}
