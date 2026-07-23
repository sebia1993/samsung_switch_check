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
        [Parameter(Mandatory = $true)][string]$ProductRelativeRoot
    )

    $baseFull = [IO.Path]::GetFullPath($BaseRoot).TrimEnd('\')
    $productFull = [IO.Path]::GetFullPath((Join-Path $baseFull $ProductRelativeRoot)).TrimEnd('\')
    $targetFull = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $targetFull.Equals($productFull, [StringComparison]::OrdinalIgnoreCase) -and
        -not $targetFull.StartsWith(($productFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "SamsungSwitchWatch 전용 안전 경로 밖입니다: $targetFull"
    }
    Assert-SswNoReparsePoint -Parent $baseFull -Child $targetFull
}

function Write-SswStep {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[Samsung Switch Watch] $Message" -ForegroundColor Cyan
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
        [ValidateSet('ReadAndExecute', 'Modify')][string]$ServiceRights
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "ACL을 설정할 폴더가 없습니다: $resolved"
    }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "junction 또는 symlink 폴더는 ACL을 자동 변경하지 않습니다: $resolved"
    }

    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $agentSid = New-Object Security.Principal.SecurityIdentifier($ServiceSid)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $descendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    $reparsePoint = $descendants | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | Select-Object -First 1
    if ($reparsePoint) {
        throw "junction 또는 symlink가 포함된 폴더 트리는 자동 변경하지 않습니다: $($reparsePoint.FullName)"
    }

    $acl = Get-Acl -LiteralPath $resolved
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($acl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
        $acl.PurgeAccessRules($identity)
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $agentSid, [Security.AccessControl.FileSystemRights]::$ServiceRights, $inheritance, $propagation, $allow)))
    Set-Acl -LiteralPath $resolved -AclObject $acl

    foreach ($item in $descendants | Sort-Object { $_.FullName.Length }) {
        $childAcl = Get-Acl -LiteralPath $item.FullName
        $childAcl.SetAccessRuleProtection($true, $false)
        foreach ($identity in @($childAcl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
            $childAcl.PurgeAccessRules($identity)
        }
        $childAcl.SetAccessRuleProtection($false, $false)
        Set-Acl -LiteralPath $item.FullName -AclObject $childAcl
    }

    $verified = Get-Acl -LiteralPath $resolved
    $allowedSids = @($systemSid.Value, $administratorsSid.Value, $agentSid.Value)
    $unexpected = @($verified.Access | Where-Object {
        $_.IsInherited -or $_.AccessControlType -ne $allow -or
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedSids
    })
    if ($unexpected.Count -gt 0) {
        throw "허용되지 않은 폴더 권한이 남아 있습니다: $resolved"
    }
    foreach ($requiredSid in $allowedSids) {
        if (-not ($verified.Access | Where-Object {
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $requiredSid
        })) {
            throw "필수 폴더 권한을 확인하지 못했습니다: $requiredSid"
        }
    }
    foreach ($item in $descendants) {
        $childAcl = Get-Acl -LiteralPath $item.FullName
        $invalidChildRule = $childAcl.Access | Where-Object {
            -not $_.IsInherited -or $_.AccessControlType -ne $allow -or
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedSids
        } | Select-Object -First 1
        if ($invalidChildRule) {
            throw "하위 항목에 허용되지 않은 명시적 권한이 남아 있습니다: $($item.FullName)"
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
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "백업 폴더가 없습니다: $resolved" }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "junction 또는 symlink 백업 폴더는 사용하지 않습니다: $resolved"
    }
    $descendants = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop)
    $reparsePoint = $descendants | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    } | Select-Object -First 1
    if ($reparsePoint) {
        throw "junction 또는 symlink가 포함된 백업 트리는 사용하지 않습니다: $($reparsePoint.FullName)"
    }

    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $allowedSids = @($systemSid.Value, $administratorsSid.Value)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $acl = Get-Acl -LiteralPath $resolved
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($acl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
        $acl.PurgeAccessRules($identity)
    }
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)))
    }
    Set-Acl -LiteralPath $resolved -AclObject $acl

    foreach ($item in $descendants | Sort-Object { $_.FullName.Length }) {
        $childAcl = Get-Acl -LiteralPath $item.FullName
        $childAcl.SetAccessRuleProtection($true, $false)
        foreach ($identity in @($childAcl.Access | ForEach-Object { $_.IdentityReference } | Select-Object -Unique)) {
            $childAcl.PurgeAccessRules($identity)
        }
        $childAcl.SetAccessRuleProtection($false, $false)
        Set-Acl -LiteralPath $item.FullName -AclObject $childAcl
    }

    $verified = Get-Acl -LiteralPath $resolved
    $unexpected = @($verified.Access | Where-Object {
        $_.IsInherited -or $_.AccessControlType -ne $allow -or
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedSids
    })
    if ($unexpected.Count -gt 0) {
        throw "허용되지 않은 백업 폴더 권한이 남아 있습니다: $resolved"
    }
    foreach ($requiredSid in $allowedSids) {
        if (-not ($verified.Access | Where-Object {
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $requiredSid
        })) {
            throw "필수 백업 폴더 권한을 확인하지 못했습니다: $requiredSid"
        }
    }
    foreach ($item in $descendants) {
        $childAcl = Get-Acl -LiteralPath $item.FullName
        $invalidChildRule = $childAcl.Access | Where-Object {
            -not $_.IsInherited -or $_.AccessControlType -ne $allow -or
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedSids
        } | Select-Object -First 1
        if ($invalidChildRule) {
            throw "하위 백업 항목에 허용되지 않은 명시적 권한이 남아 있습니다: $($item.FullName)"
        }
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
            if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $journalPath
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $replaceBackup -PathType Leaf) { Remove-Item -LiteralPath $replaceBackup -Force }
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
    $actual = @($Snapshot.RemoteAddress | ForEach-Object { [string]$_ } | Sort-Object)
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
